using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Query;
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

    [Fact]
    public async Task EmitAsync_EmitsOneOccurrencePerReference_AndKeepsDistinctCallSitesApart()
    {
        // Collapsing occurrences that agree on position is not enough. A qualified
        // call is spelled by a chain of nodes that resolve to the same symbol at
        // different columns: for `Helper.Do()` the invocation and the member access
        // start at `Helper`, and the identifier `Do` starts seven characters later.
        // One call therefore arrived as two occurrences, so refs roughly doubled its
        // count for every qualified call in the codebase, which is the number an
        // agent uses to size a change.
        //
        // The other direction has to hold too: collapsing must be about one reference
        // spelled by several nodes, never about two references that happen to share a
        // line, so `Helper.Do() + Helper.Do()` must stay two.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "Caller.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            public static class Helper
            {
                public static int Do() => 0;
            }

            public class Caller
            {
                public int Go()
                {
                    Helper.Do();
                    return Helper.Do() + Helper.Do();
                }
            }
            #line default
            """);

        var index = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var references = occurrences
            .Where(o => o.Symbol == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0)
            .Select(o => (Line: o.Range[0], Character: o.Range[1]))
            .OrderBy(o => o.Line).ThenBy(o => o.Character)
            .ToList();

        // Three call sites in the source, so three references and no more.
        Assert.Equal(3, references.Count);

        // Mapped lines are zero-based, and `#line 1` puts the first source line at 0:
        //
        //     9:  "        Helper.Do();"
        //     10: "        return Helper.Do() + Helper.Do();"
        //
        // Line 9 is one call, and one hit. The canonical position is the identifier
        // that names the symbol, `Do` at character 15, not the receiver at 8.
        Assert.Equal(new[] { (9, 15) }, references.Where(r => r.Line == 9).ToArray());

        // Line 10 is two calls, and two hits at two distinct columns.
        Assert.Equal(new[] { (10, 22), (10, 36) }, references.Where(r => r.Line == 10).ToArray());

        // The receiver is a different symbol at a different position, and folding a
        // reference onto its name node must not lose it.
        Assert.Contains(occurrences, o =>
            o.Symbol == "Helper" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0);

        // And the definition is still exactly one, still where it was.
        Assert.Single(occurrences.Where(o =>
            o.Symbol == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));
    }

    [Fact]
    public async Task EmitAsync_DoesNotLetALocalVariableBecomeACaller()
    {
        // impact answers "who calls this", and it answers it from stored enclosing
        // ranges: the innermost definition whose range contains the reference is the
        // caller. GetDeclaredSymbol succeeds on a VariableDeclaratorSyntax, so a local
        // was being recorded as a definition with a range of its own, and that range is
        // the innermost one around the very reference that initialises it. The verified
        // case is below: `impact Perfume.Status` named the local `status` as the caller
        // of the property it is assigned from. A blast radius that mixes real callers
        // with local-variable names cannot be told apart by the reader, and every name
        // in it is a name an agent acts on.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "PerfumeService.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            public class Perfume
            {
                public string Status { get; set; } = "";
            }

            public class PerfumeService
            {
                public void Publish(Perfume perfume)
                {
                    var status = perfume.Status;
                }
            }
            #line default
            """);

        var index = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        // The local is still indexed - it is a real declaration and `def` should find
        // it - but it carries no body range, so it can never enclose anything.
        var local = Assert.Single(occurrences.Where(o =>
            o.Symbol.EndsWith("status", StringComparison.Ordinal)
            && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));
        Assert.Empty(local.EnclosingRange);

        // The parameter is the same shape of declaration and must be treated the same.
        var parameter = Assert.Single(occurrences.Where(o =>
            o.Symbol.EndsWith("perfume", StringComparison.Ordinal)
            && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));
        Assert.Empty(parameter.EnclosingRange);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        var hits = ImpactQuery.Run(db, "Perfume.Status");

        var hit = Assert.Single(hits);
        Assert.Equal("PerfumeService.Publish(Perfume)", hit.Symbol);
    }

    [Fact]
    public async Task EmitAsync_GivesLocalsAndParametersInDifferentMethodsDifferentIdentities()
    {
        // SymbolIdentity.For used one display format for everything, and that format
        // has nothing to qualify a local with: an ILocalSymbol rendered as the bare
        // name `count`, and an IParameterSymbol as `System.Int32 count`. Two methods
        // each declaring `int count` therefore collapsed into ONE symbol, so
        // `refs count` returned every local of that name in the solution as though
        // they were one variable, and the count - the number an agent uses to size a
        // change - was the sum of unrelated things.
        //
        // The identity format for types and members is a deliberate, documented
        // decision that other behaviour depends on, so it is untouched. Only the kinds
        // that have no containing-type qualification of their own gain one.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "Counter.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            public class Counter
            {
                public int First()
                {
                    int count = 1;
                    return count;
                }

                public int Second()
                {
                    int count = 2;
                    return count;
                }

                public int Third(int count) => count;
            }
            #line default
            """);

        var index = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var locals = occurrences
            .Where(o => (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0
                        && o.Symbol.EndsWith("count", StringComparison.Ordinal))
            .Select(o => o.Symbol)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // Two locals and one parameter, all called count, all distinct.
        Assert.Equal(3, locals.Count);
        Assert.Contains("Counter.First().count", locals);
        Assert.Contains("Counter.Second().count", locals);
        Assert.Contains("Counter.Third(System.Int32).count", locals);

        // Types and members keep exactly the identity format they had.
        var symbols = occurrences.Select(o => o.Symbol).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Counter", symbols);
        Assert.Contains("Counter.First()", symbols);
        Assert.Contains("Counter.Third(System.Int32)", symbols);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        // The consequence that matters: asking about one method's local no longer
        // answers with another method's.
        var first = RefsQuery.Run(db, "First().count");
        Assert.NotEmpty(first);
        Assert.All(first, h => Assert.Equal("Counter.First().count", h.Symbol));
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
