using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Vela.Harvest;
using Vela.Tests.Fixtures;
using Xunit;

public class ScipEmitterTests
{
    [Fact]
    public async Task EmitAsync_ProducesADocumentForEveryRazorView()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        var razor = index.Documents
            .Where(d => d.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(fx.RazorFileCount, razor.Count);

        // Document seeding alone would satisfy the count above, so a total collapse of
        // Razor occurrence mapping would leave seven empty .cshtml documents and a
        // green test. The views must actually carry occurrences.
        Assert.True(razor.Sum(d => d.Occurrences.Count) > 0,
            "Razor documents exist but carry no occurrences, which is the whole point of the tool");
    }

    [Fact]
    public async Task EmitAsync_RecordsEnclosingRangeOnDefinitions()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        var withEnclosure = index.Documents
            .SelectMany(d => d.Occurrences)
            .Where(o => o.EnclosingRange.Count > 0)
            .ToList();

        Assert.True(withEnclosure.Count > 0,
            "enclosing_range is what makes callers a stored edge rather than an inference");

        foreach (var occurrence in withEnclosure)
        {
            // Only a definition may carry an enclosing range: it is the body of the
            // thing being defined.
            Assert.True((occurrence.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0,
                $"occurrence of '{occurrence.Symbol}' carries an enclosing range without the Definition role");

            // start line, start character, end line, end character.
            Assert.Equal(4, occurrence.EnclosingRange.Count);

            var startLine = occurrence.EnclosingRange[0];
            var startCharacter = occurrence.EnclosingRange[1];
            var endLine = occurrence.EnclosingRange[2];
            var endCharacter = occurrence.EnclosingRange[3];

            // The range opens where the occurrence itself does, in the same document.
            Assert.Equal(occurrence.Range[0], startLine);
            Assert.Equal(occurrence.Range[1], startCharacter);

            // The end must not precede the start. Razor #line regions are not
            // monotonic, so this is a real possibility, not a tautology.
            Assert.True(endLine > startLine || (endLine == startLine && endCharacter >= startCharacter),
                $"enclosing range of '{occurrence.Symbol}' ends at {endLine}:{endCharacter}, "
                + $"before it starts at {startLine}:{startCharacter}");
        }
    }

    [Fact]
    public async Task EmitAsync_ConformsDocumentPathsAndEncodingToTheScipSpec()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        Assert.NotEmpty(index.Documents);

        foreach (var doc in index.Documents)
        {
            // scip.proto, Document.relative_path: relative to project_root, no leading
            // '/', '/' as the separator on every platform, no '.' or '..' components.
            Assert.DoesNotContain("\\", doc.RelativePath, StringComparison.Ordinal);
            Assert.False(doc.RelativePath.StartsWith('/'), $"'{doc.RelativePath}' is not relative");
            Assert.DoesNotContain("..", doc.RelativePath.Split('/'));
            Assert.DoesNotContain(".", doc.RelativePath.Split('/'));

            // scip.proto, PositionEncoding: the unspecified value must not be used by
            // new indexers, and Roslyn offsets are UTF-16 code units.
            Assert.Equal(Scip.PositionEncoding.Utf16CodeUnitOffsetFromLineStart, doc.PositionEncoding);
        }

        Assert.Equal(Scip.TextEncoding.Utf8, index.Metadata.TextDocumentEncoding);
    }

    [Fact]
    public async Task EmitAsync_OmitsEnclosingRangeWhenTheDefinitionEndMapsToAnotherFile()
    {
        var root = SyntheticRoot();
        var view = Path.Combine(root, "App", "Pages", "Index.cshtml");
        var imports = Path.Combine(root, "App", "Pages", "_ViewImports.cshtml");

        // A generated Razor tree whose #line regions are not monotonic: the class
        // opens inside Index.cshtml and closes inside _ViewImports.cshtml.
        var solution = SyntheticSolution(root, $$"""
            #line 5 "{{Escape(view)}}"
            public class Straddler
            {
            #line 2 "{{Escape(imports)}}"
                public void Contained() { }
            }
            #line default
            """);

        var index = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var straddler = occurrences.Single(o => o.Symbol == "Straddler");
        Assert.Empty(straddler.EnclosingRange);

        // A definition that stays inside one file keeps its range, so the guard is
        // not simply switching enclosing ranges off.
        var contained = occurrences.Single(o => o.Symbol == "Straddler.Contained()");
        Assert.Equal(4, contained.EnclosingRange.Count);
    }

    [Fact]
    public async Task EmitAsync_OmitsEnclosingRangeWhenTheDefinitionEndPrecedesItsStart()
    {
        var root = SyntheticRoot();
        var view = Path.Combine(root, "App", "Pages", "Index.cshtml");

        // Same file at both ends, but the second #line region rewinds to an earlier
        // line, so the end of the class maps above its own start.
        var solution = SyntheticSolution(root, $$"""
            #line 30 "{{Escape(view)}}"
            public class Inverted
            {
            #line 3 "{{Escape(view)}}"
                public void Contained() { }
            }
            #line default
            """);

        var index = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var inverted = occurrences.Single(o => o.Symbol == "Inverted");
        Assert.Empty(inverted.EnclosingRange);

        var contained = occurrences.Single(o => o.Symbol == "Inverted.Contained()");
        Assert.Equal(4, contained.EnclosingRange.Count);
    }

    [Fact]
    public async Task EmitAsync_RecordsFilesOutsideTheProjectRootInsteadOfEmittingDotDotPaths()
    {
        var root = SyntheticRoot();
        var outside = Path.Combine(SyntheticRoot(), "Lib", "External.cshtml");

        var solution = SyntheticSolution(root, $$"""
            #line 4 "{{Escape(outside)}}"
            public class Outsider { }
            #line default
            """);

        var index = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);

        // scip.proto forbids a relative_path that escapes project_root, so the file
        // cannot be emitted as a document.
        Assert.DoesNotContain(index.Documents, d => d.RelativePath.Contains(".."));
        Assert.DoesNotContain(index.Documents, d => Path.IsPathRooted(d.RelativePath));

        // Constraint 3: the omission has to be visible in the index it happened in.
        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.Contains("outside-project-root") && a.Contains("External.cshtml"));
    }

    [Fact]
    public async Task EmitAsync_EmitsEachOccurrenceOnceInADocument()
    {
        // The emitter walks every descendant node, and several nodes can resolve to
        // the same symbol at the same position: an invocation and the member access
        // it is made through share a SpanStart and a symbol, so a single call site
        // was being recorded twice. The damage is downstream: refs prints the same
        // hit twice and reports a count that is wrong in the tool's most used verb,
        // and an agent counting call sites doubles them.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "Caller.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            public static class Helper
            {
                public static void Do() { }
            }

            public class Caller
            {
                public void Go()
                {
                    Helper.Do();
                }
            }
            #line default
            """);

        var index = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);

        var duplicates = index.Documents
            .SelectMany(d => d.Occurrences.Select(o => new
            {
                d.RelativePath,
                o.Symbol,
                Role = o.SymbolRoles,
                Range = string.Join(',', o.Range),
                Enclosing = string.Join(',', o.EnclosingRange)
            }))
            .GroupBy(o => o)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.RelativePath} {g.Key.Symbol} role={g.Key.Role} range={g.Key.Range} x{g.Count()}")
            .ToList();

        Assert.Empty(duplicates);

        // The facts themselves still have to be there. A dedup that dropped the
        // occurrence altogether would also satisfy the assertion above, and an empty
        // answer is the reading that does real damage (Constraint 3). Deduplication
        // is exact: it collapses one position recorded twice, never two positions.
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        Assert.NotEmpty(occurrences.Where(o =>
            o.Symbol == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0));

        Assert.Single(occurrences.Where(o =>
            o.Symbol == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));
    }

    private static string SyntheticRoot() =>
        Path.Combine(Path.GetTempPath(), "vela-synth-" + Guid.NewGuid().ToString("N")[..8]);

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    /// <summary>
    /// An in-memory solution holding one generated-looking C# file. Nothing is written
    /// to disk: the emitter only ever reads paths, never their contents.
    /// </summary>
    private static Solution SyntheticSolution(string root, string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var document = DocumentInfo.Create(
            documentId,
            "Generated.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default)),
            filePath: Path.Combine(root, "App", "obj", "Generated.cs"));

        var project = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Synthetic",
            assemblyName: "Synthetic",
            language: LanguageNames.CSharp,
            filePath: Path.Combine(root, "App", "App.csproj"),
            documents: new[] { document },
            metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        var solution = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Default,
            filePath: Path.Combine(root, "Synthetic.sln"),
            projects: new[] { project });

        return workspace.AddSolution(solution);
    }
}
