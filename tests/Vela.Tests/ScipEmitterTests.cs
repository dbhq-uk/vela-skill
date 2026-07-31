using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

// The tests that emit over the scaffolded Razor Pages app all read the same emission and
// none of them changes it, so the class emits once and they share it. The rest of the
// class builds its own compilations in memory and needs no fixture at all.
public class ScipEmitterTests : IClassFixture<HarvestedWebApp>
{
    private readonly HarvestedWebApp _webApp;

    public ScipEmitterTests(HarvestedWebApp webApp) => _webApp = webApp;

    [Fact]
    public void EmitAsync_ProducesADocumentForEveryRazorView()
    {
        var index = _webApp.Emitted.Index;

        var razor = index.Documents
            .Where(d => d.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(_webApp.RazorFileCount, razor.Count);

        // Document seeding alone would satisfy the count above, so a total collapse of
        // Razor occurrence mapping would leave seven empty .cshtml documents and a
        // green test. The views must actually carry occurrences.
        Assert.True(razor.Sum(d => d.Occurrences.Count) > 0,
            "Razor documents exist but carry no occurrences, which is the whole point of the tool");

        // project_root is the repository root when there is one, and the solution
        // directory when there is not. This fixture is a temp directory under no
        // repository, so every path stays relative to the solution directory: the
        // fallback is what keeps a relative_path meaning what it meant before, and a
        // root that silently widened to the temp directory would prefix every path
        // with a random fixture name.
        Assert.All(razor, d => Assert.StartsWith("App/", d.RelativePath, StringComparison.Ordinal));
    }

    [Fact]
    public void EmitAsync_MarksGeneratedDocumentsThatAreNotOnDisk_AndOnlyThose()
    {
        // The Razor generator does not write its output to disk unless
        // EmitCompilerGeneratedFiles is set, so the .g.cs documents in the index name
        // paths that do not exist. The originating .cshtml does exist and must never be
        // marked, or refs would suppress the very thing vela is for.
        var emitted = _webApp.Emitted;

        Assert.NotEmpty(emitted.GeneratedDocuments);

        foreach (var relativePath in emitted.GeneratedDocuments)
        {
            Assert.EndsWith(".g.cs", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(_webApp.Root, relativePath)),
                $"'{relativePath}' is marked generated but exists on disk, so it is openable "
                + "and suppressing it would hide a real location");
        }

        // Every view, and every hand-written .cs file, stays openable and unmarked.
        foreach (var doc in emitted.Index.Documents)
        {
            if (doc.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                Assert.DoesNotContain(doc.RelativePath, emitted.GeneratedDocuments);
        }
    }

    [Fact]
    public void EmitAsync_RecordsEnclosingRangeOnDefinitions()
    {
        var index = _webApp.Emitted.Index;

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
    public void EmitAsync_ConformsDocumentPathsAndEncodingToTheScipSpec()
    {
        var index = _webApp.Emitted.Index;

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
    public void EmitAsync_PutsAScipSymbolOnTheWireAndKeepsTheDisplayNameBesideIt()
    {
        var emitted = _webApp.Emitted;
        var occurrences = emitted.Index.Documents.SelectMany(d => d.Occurrences).ToList();

        Assert.NotEmpty(occurrences);

        // Occurrence.symbol is the wire format, so every one of them has to be a
        // sentence in the grammar scip.proto specifies, not a Roslyn display string.
        // The field is optional, and an occurrence vela can form no honest moniker for
        // carries none rather than a false `local` - a string[] and the global namespace
        // are reachable from every document in the solution, so the local form would be
        // a claim the spec forbids. That is the one value the grammar has no sentence
        // for, because it is the absence of one.
        var named = occurrences.Where(o => o.Symbol.Length > 0).ToList();
        var unnamed = occurrences.Where(o => o.Symbol.Length == 0).ToList();

        foreach (var symbol in named.Select(o => o.Symbol).Distinct(StringComparer.Ordinal))
            ScipSymbolGrammar.RoundTrip(symbol);

        Assert.NotEmpty(unnamed);
        Assert.True(named.Count > unnamed.Count * 5, $"{unnamed.Count} of {occurrences.Count} carry no moniker");

        // And they are exactly the two things in this fixture that SCIP cannot name and
        // that are not document-local either: the global namespace, which vela renders
        // as the empty display name and which every `global::` in a generated Razor view
        // resolves to, and `dynamic`, which the Razor generator spells in every view.
        // Anything else appearing here would be a symbol that lost a name it could have
        // had.
        Assert.Equal(
            new[] { "", "dynamic" },
            unnamed.Select(emitted.DisplayNameOf).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        // And the display name is still there, beside it. It is what the query layer
        // matches on and it cannot be read back off the moniker, so every occurrence
        // has to carry its own: DisplayNameOf falls back to the SCIP symbol when it
        // does not, and no display name is ever a SCIP symbol.
        Assert.All(named, o => Assert.NotEqual(o.Symbol, emitted.DisplayNameOf(o)));

        // vela renders the global namespace as the empty string, which is what
        // `global::` in generated Razor resolves to, and that is unchanged here. What
        // must not happen is the moniker taking its place.
        Assert.All(
            occurrences.Where(o => emitted.DisplayNameOf(o).Length == 0),
            o => Assert.Empty(o.Symbol));

        var viewData = occurrences
            .Where(o => emitted.DisplayNameOf(o).EndsWith(".ViewData", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(viewData);
        Assert.All(viewData, o => Assert.EndsWith("ViewData.", o.Symbol, StringComparison.Ordinal));
        Assert.All(viewData, o => Assert.StartsWith("scip-dotnet nuget ", o.Symbol, StringComparison.Ordinal));
    }

    [Fact]
    public void EmitAsync_KeepsEveryLocalSymbolInsideOneDocument()
    {
        // scip.proto: "Local symbols MUST only be used for entities which are local to
        // a Document, and cannot be accessed from outside the Document." A local id
        // that turned up in two documents would be claiming two unrelated things are
        // the same, which is exactly what the id form cannot express.
        var emitted = _webApp.Emitted;

        var documentsPerLocal = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var doc in emitted.Index.Documents)
        {
            foreach (var occurrence in doc.Occurrences)
            {
                if (!occurrence.Symbol.StartsWith("local ", StringComparison.Ordinal)) continue;

                if (!documentsPerLocal.TryGetValue(occurrence.Symbol, out var documents))
                    documentsPerLocal[occurrence.Symbol] = documents = new HashSet<string>(StringComparer.Ordinal);
                documents.Add(doc.RelativePath);
            }
        }

        Assert.NotEmpty(documentsPerLocal);

        var shared = documentsPerLocal
            .Where(entry => entry.Value.Count > 1)
            .Select(entry => $"{entry.Key} appears in {string.Join(", ", entry.Value.Order(StringComparer.Ordinal))}")
            .ToList();

        Assert.True(shared.Count == 0, string.Join("\n", shared));
    }

    [Fact]
    public async Task EmitAsync_DescribesTheSymbolsEachDocumentDefines()
    {
        // scip.proto's Document.symbols: "Symbols that are 'defined' within this
        // document." Without them a consumer has the positions and no idea what any of
        // them is, and has to run a compiler of its own to find out.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "Described.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            namespace App;

            /// <summary>
            /// What a perfume smells of.
            /// </summary>
            public interface IScent
            {
                string Note { get; }
            }

            public class Perfume : IScent
            {
                public string Note => "amber";
                public const int Limit = 3;
                public static void Publish() { }
            }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var doc = Assert.Single(
            emitted.Index.Documents, d => d.RelativePath.EndsWith("Described.cs", StringComparison.Ordinal));

        Assert.NotEmpty(doc.Symbols);

        // Described once each, and only what this document defines.
        Assert.Equal(
            doc.Symbols.Select(s => s.Symbol).Distinct(StringComparer.Ordinal).Count(),
            doc.Symbols.Count);

        var definitions = doc.Occurrences
            .Where(o => (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0)
            .Select(o => o.Symbol)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(doc.Symbols, s => Assert.Contains(s.Symbol, definitions));

        // Found by moniker, because that is the unique one. display_name is the short
        // name scip.proto asks for - "the symbol 'com/example/MyClass#myMethod(+1).'
        // should have the display name 'myMethod'" - and IScent.Note and Perfume.Note
        // are two symbols with one short name, which is the whole reason the field is
        // not an identity.
        var described = doc.Symbols.ToDictionary(s => s.Symbol, StringComparer.Ordinal);
        Scip.SymbolInformation Described(string monikerSuffix) => Assert.Single(
            described.Values, s => s.Symbol.EndsWith(monikerSuffix, StringComparison.Ordinal));

        Assert.Equal(Scip.SymbolInformation.Types.Kind.Interface, Described("App/IScent#").Kind);
        Assert.Equal(Scip.SymbolInformation.Types.Kind.Class, Described("App/Perfume#").Kind);
        Assert.Equal(Scip.SymbolInformation.Types.Kind.Property, Described("App/Perfume#Note.").Kind);
        Assert.Equal(Scip.SymbolInformation.Types.Kind.Constant, Described("App/Perfume#Limit.").Kind);
        Assert.Equal(Scip.SymbolInformation.Types.Kind.StaticMethod, Described("App/Perfume#Publish().").Kind);
        Assert.Equal(Scip.SymbolInformation.Types.Kind.Namespace, Described(" App/").Kind);

        Assert.Equal("IScent", Described("App/IScent#").DisplayName);
        Assert.Equal("Perfume", Described("App/Perfume#").DisplayName);
        Assert.Equal("Note", Described("App/Perfume#Note.").DisplayName);
        Assert.Equal("Note", Described("App/IScent#Note.").DisplayName);
        Assert.Equal("Limit", Described("App/Perfume#Limit.").DisplayName);
        Assert.Equal("Publish", Described("App/Perfume#Publish().").DisplayName);
        Assert.Equal("App", Described(" App/").DisplayName);

        // The doc comment, as prose rather than as a rendered signature, which is what
        // scip.proto asks new indexers for.
        Assert.Equal("What a perfume smells of.", Assert.Single(Described("App/IScent#").Documentation));
        Assert.Empty(Described("App/Perfume#").Documentation);

        foreach (var information in doc.Symbols)
            ScipSymbolGrammar.RoundTrip(information.Symbol);
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

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var straddler = occurrences.Single(o => emitted.DisplayNameOf(o) == "Straddler");
        Assert.Empty(straddler.EnclosingRange);

        // A definition that stays inside one file keeps its range, so the guard is
        // not simply switching enclosing ranges off.
        var contained = occurrences.Single(o => emitted.DisplayNameOf(o) == "Straddler.Contained()");
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

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var inverted = occurrences.Single(o => emitted.DisplayNameOf(o) == "Inverted");
        Assert.Empty(inverted.EnclosingRange);

        var contained = occurrences.Single(o => emitted.DisplayNameOf(o) == "Inverted.Contained()");
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

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        // scip.proto forbids a relative_path that escapes project_root, so the file
        // cannot be emitted as a document.
        Assert.DoesNotContain(index.Documents, d => d.RelativePath.Contains(".."));
        Assert.DoesNotContain(index.Documents, d => Path.IsPathRooted(d.RelativePath));

        // Constraint 3: the omission has to be visible in the index it happened in.
        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.Contains("outside-project-root") && a.Contains("External.cshtml"));
    }

    [Fact]
    public async Task EmitAsync_KeepsAFirstPartyFileOutsideTheRepositoryAsALoudGap()
    {
        // Being outside the repository is not evidence that a file belongs to somebody
        // else. `<Compile Include="..\..\Shared\Foo.cs" />` is an ordinary way to share
        // source between two repositories, a .sln that references a project above its
        // own root does the same thing a project at a time, and a submodule makes the
        // parent repository's source "outside" because the innermost .git wins. All
        // three are first-party code that is genuinely absent from the index, and
        // demoting them to informational made a whole project vanish with health clean
        // and exit 0, which is the exact failure Constraint 3 exists to forbid.
        using var repository = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        using var elsewhere = new TempDirectory();
        var linked = Path.Combine(elsewhere.Path, "Shared", "Foo.cs");

        var solution = SyntheticSolution(repository.Path, $$"""
            #line 1 "{{Escape(linked)}}"
            public class Foo { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal) && a.Contains("Foo.cs"));
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal));

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());
        Assert.True(health.Degraded, "first-party code missing from the index is a gap in it");
        Assert.Contains("Foo.cs", health.Detail);
        Assert.Equal(0, Program.CountExternalDocuments(index));
    }

    [Fact]
    public async Task EmitAsync_TreatsTheDotnetInstallationAsExternalRatherThanAsAGap()
    {
        // The other known location whose contents are nobody's first-party code: the
        // shared runtime and the SDK vela is running on. Files from there are in the
        // compilation, cannot sit under project_root, and are not a gap in anybody's
        // repository, so they are named and counted rather than reported as missing.
        using var repository = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var shared = runtime.Parent?.Parent;

        // A framework-dependent .NET lays the runtime out as
        // <dotnet>/shared/<framework>/<version>, which is how the install root is
        // found. Asserted rather than assumed, so an unexpected layout says so here
        // instead of quietly testing nothing.
        Assert.Equal("shared", shared?.Name);
        var dotnetRoot = shared!.Parent!.FullName;

        var fromRuntime = Path.Combine(runtime.FullName, "TheSharedFramework.cs");
        var fromSdk = Path.Combine(dotnetRoot, "sdk", "10.0.100", "Sdks", "Microsoft.NET.Sdk", "Sdk.cs");

        var solution = SyntheticSolution(repository.Path, $$"""
            #line 1 "{{Escape(fromRuntime)}}"
            public class FromRuntime { }
            #line 1 "{{Escape(fromSdk)}}"
            public class FromSdk { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("TheSharedFramework.cs"));
        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal) && a.Contains("Sdk.cs"));
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal));

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());
        Assert.False(health.Degraded, health.Detail);
        Assert.Equal(2, Program.CountExternalDocuments(index));
    }

    [Fact]
    public async Task EmitAsync_KeepsAFileItCannotProveIsSomebodyElsesAsALoudGap()
    {
        // The other half of the split, and the half that must never soften. A file
        // outside project_root that is in no package cache and no SDK may well be the
        // user's own code sitting one directory up, and here there is not even a
        // repository boundary to reason about. Recording that as informational would
        // hide a real coverage gap, which is exactly what Constraint 3 exists to
        // prevent.
        var root = SyntheticRoot();
        var outside = Path.Combine(SyntheticRoot(), "Lib", "External.cshtml");

        var solution = SyntheticSolution(root, $$"""
            #line 4 "{{Escape(outside)}}"
            public class Outsider { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal) && a.Contains("External.cshtml"));
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal));

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());
        Assert.True(health.Degraded);
        Assert.Contains("External.cshtml", health.Detail);
        Assert.Equal(0, Program.CountExternalDocuments(index));
    }

    [Fact]
    public async Task EmitAsync_RootsTheIndexAtTheRepositoryRootSoASolutionInASubdirectoryStillCoversIt()
    {
        // repo/src/App.sln is an ordinary layout, and project_root at the solution
        // directory stranded everything above src/: a shared view, a linked file or a
        // project outside src/ could not be a document at all, so it was reported as a
        // gap rather than indexed. The repository is the unit a developer thinks in and
        // the unit a change is made in, so that is what the index is rooted at.
        using var repository = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        var solutionDirectory = Path.Combine(repository.Path, "src");
        Directory.CreateDirectory(solutionDirectory);

        var shared = Path.Combine(repository.Path, "lib", "Shared", "Widget.cshtml");

        var solution = SyntheticSolution(solutionDirectory, $$"""
            #line 4 "{{Escape(shared)}}"
            public class Widget { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Equal(new Uri(repository.Path).AbsoluteUri, index.Metadata.ProjectRoot);

        var document = Assert.Single(index.Documents,
            d => d.RelativePath.EndsWith("Widget.cshtml", StringComparison.Ordinal));
        Assert.Equal("lib/Shared/Widget.cshtml", document.RelativePath);

        // Indexed rather than reported: there is nothing left to be degraded about.
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal)
                 || a.StartsWith("external-document:", StringComparison.Ordinal));
        Assert.False(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
    }

    [Fact]
    public async Task EmitAsync_TreatsAGitFileAsARepositoryRootBecauseThatIsWhatAWorktreeHas()
    {
        // A linked worktree, and a submodule, carry a .git FILE holding a `gitdir:`
        // pointer rather than a .git directory. Stopping the walk only on a directory
        // would root a worktree's index somewhere above the worktree, or nowhere at
        // all, and vela's own development is done in worktrees.
        using var repository = new TempDirectory();
        File.WriteAllText(Path.Combine(repository.Path, ".git"), "gitdir: /elsewhere/.git/worktrees/w\n");

        var solutionDirectory = Path.Combine(repository.Path, "src");
        Directory.CreateDirectory(solutionDirectory);

        var shared = Path.Combine(repository.Path, "lib", "Shared", "Widget.cshtml");

        var solution = SyntheticSolution(solutionDirectory, $$"""
            #line 4 "{{Escape(shared)}}"
            public class Widget { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Equal(new Uri(repository.Path).AbsoluteUri, index.Metadata.ProjectRoot);

        var document = Assert.Single(index.Documents,
            d => d.RelativePath.EndsWith("Widget.cshtml", StringComparison.Ordinal));
        Assert.Equal("lib/Shared/Widget.cshtml", document.RelativePath);
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

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        var duplicates = index.Documents
            .SelectMany(d => d.Occurrences.Select(o => new
            {
                d.RelativePath,
                Symbol = emitted.DisplayNameOf(o),
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

        Assert.Contains(occurrences, o =>
            emitted.DisplayNameOf(o) == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0);

        Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o) == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);
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

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var references = occurrences
            .Where(o => emitted.DisplayNameOf(o) == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0)
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
            emitted.DisplayNameOf(o) == "Helper" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0);

        // And the definition is still exactly one, still where it was.
        Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o) == "Helper.Do()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);
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

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        // The local is still indexed - it is a real declaration and `def` should find
        // it - but it carries no body range, so it can never enclose anything.
        var local = Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o).EndsWith("status", StringComparison.Ordinal)
            && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);
        Assert.Empty(local.EnclosingRange);

        // The parameter is the same shape of declaration and must be treated the same.
        var parameter = Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o).EndsWith("perfume", StringComparison.Ordinal)
            && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);
        Assert.Empty(parameter.EnclosingRange);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, emitted);

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

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var locals = occurrences
            .Where(o => (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0
                        && emitted.DisplayNameOf(o).EndsWith("count", StringComparison.Ordinal))
            .Select(emitted.DisplayNameOf)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // Two locals and one parameter, all called count, all distinct.
        Assert.Equal(3, locals.Count);
        Assert.Contains("Counter.First().count", locals);
        Assert.Contains("Counter.Second().count", locals);
        Assert.Contains("Counter.Third(System.Int32).count", locals);

        // Types and members keep exactly the identity format they had.
        var symbols = occurrences.Select(emitted.DisplayNameOf).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Counter", symbols);
        Assert.Contains("Counter.First()", symbols);
        Assert.Contains("Counter.Third(System.Int32)", symbols);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, emitted);

        // The consequence that matters: asking about one method's local no longer
        // answers with another method's.
        var first = RefsQuery.Run(db, "First().count");
        Assert.NotEmpty(first);
        Assert.All(first, h => Assert.Equal("Counter.First().count", h.Symbol));
    }

    [Fact]
    public async Task EmitAsync_RecordsCompilationErrorsSoTheIndexCannotLookComplete()
    {
        // Constraint 3's exact failure mode, and the quietest one there is. A project
        // that loads but does not compile - one unresolved reference, a restore that
        // did not run - yields a null symbol for every node that touches the missing
        // type. ScipEmitter skips null symbols, so those references simply are not in
        // the index, no load failure was raised, and health reported clean. `refs` on
        // an affected symbol then answers confidently and short.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "Broken.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            public class Broken
            {
                public Nonexistent Field;
            }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        // Recorded through the same visible channel as load-failure and
        // outside-project-root, so there is one place a reader has to look.
        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("compile-error:", StringComparison.Ordinal));

        // And it has to reach the record the banner and the exit code are read from.
        var health = Program.BuildHealthRecord(index, Array.Empty<string>());
        Assert.True(health.Degraded);
        Assert.NotNull(health.Detail);
        Assert.Contains("compile-error:", health.Detail, StringComparison.Ordinal);
        Assert.Contains("CS0246", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitAsync_DoesNotReportCompilationErrorsForCodeThatCompiles()
    {
        // The other half: a degradation signal that fires on healthy solutions is a
        // signal nobody reads, which is the same outcome as not having it.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "Fine.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            public class Fine
            {
                public int Value { get; set; }
            }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        var reported = index.Metadata.ToolInfo.Arguments
            .Where(a => a.StartsWith("compile-error:", StringComparison.Ordinal))
            .ToList();

        Assert.True(reported.Count == 0, string.Join("\n", reported));
        Assert.False(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
    }

    [Fact]
    public async Task EmitAsync_AnchorsADefinitionAtItsIdentifier_NotAtItsAttributeList()
    {
        // A definition was recorded at node.SpanStart, and a MethodDeclarationSyntax
        // begins at its attribute list. So `def` on `[HttpPost] public IActionResult
        // Foo()` sent the reader to the line holding `[HttpPost]`, which is not where
        // anyone looking for the declaration expects to land, and on a method with
        // several attributes is several lines away from it.
        var root = SyntheticRoot();
        var file = Path.Combine(root, "App", "Controller.cs");

        var solution = SyntheticSolution(root, $$"""
            #line 1 "{{Escape(file)}}"
            public class Controller
            {
                [System.Obsolete]
                public int Foo() => 0;
            }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var method = Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o) == "Controller.Foo()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);

        // Mapped lines are zero-based and `#line 1` puts the first source line at 0, so
        // the attribute is line 2 and the declaration line 3, with `Foo` at column 15.
        Assert.Equal(3, method.Range[0]);
        Assert.Equal(15, method.Range[1]);

        // A type is anchored at its name for the same reason.
        var type = Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o) == "Controller" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);
        Assert.Equal(0, type.Range[0]);
        Assert.Equal(13, type.Range[1]);

        // The body range still opens where the occurrence does, which is what impact
        // reads, so moving the anchor must move both together.
        Assert.Equal(method.Range[0], method.EnclosingRange[0]);
        Assert.Equal(method.Range[1], method.EnclosingRange[1]);
    }

    [Fact]
    public async Task EmitAsync_FoldsAQualifiedVisualBasicCallIntoOneOccurrence()
    {
        // NamingNode matched Microsoft.CodeAnalysis.CSharp.Syntax types only, so in a
        // VB project the invocation, the member access and the identifier were never
        // folded and every qualified reference was counted roughly twice. README,
        // AGENTS.md and the plugin manifest all advertise Visual Basic, so this was a
        // wrong count in a language the tool claims to cover, in its most used verb.
        //
        // The VB syntax kinds mirror the C# ones exactly, which is why this is worth
        // implementing rather than documenting away.
        var root = SyntheticRoot();

        var solution = SyntheticVisualBasicSolution(root, """
            Public Module Helper
                Public Sub DoIt()
                End Sub
            End Module

            Public Class Caller
                Public Sub Go()
                    Helper.DoIt()
                End Sub
            End Class
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var references = occurrences
            .Where(o => emitted.DisplayNameOf(o) == "Helper.DoIt()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0)
            .Select(o => (Line: o.Range[0], Character: o.Range[1]))
            .ToList();

        // One call site, one reference. Zero-based: the call is on source line 7, and
        // the canonical position is the identifier `DoIt` at column 15, not the
        // receiver at column 8.
        var reference = Assert.Single(references);
        Assert.Equal((7, 15), reference);

        // Folding a reference onto its name must not lose the receiver, which is a
        // different symbol at a different position.
        Assert.Contains(occurrences, o =>
            emitted.DisplayNameOf(o) == "Helper" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0);

        // A VB declaration is two nodes, a block and the statement that opens it, and
        // GetDeclaredSymbol answers on both. Exactly one definition, at the identifier:
        // line 1, column 15 for `DoIt`, and line 0, column 14 for `Helper`.
        var definition = Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o) == "Helper.DoIt()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);
        Assert.Equal(1, definition.Range[0]);
        Assert.Equal(15, definition.Range[1]);

        var module = Assert.Single(occurrences, o =>
            emitted.DisplayNameOf(o) == "Helper" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0);
        Assert.Equal(0, module.Range[0]);
        Assert.Equal(14, module.Range[1]);
    }

    /// <summary>
    /// An in-memory Visual Basic solution holding one file. VB has no #line directive,
    /// so the document's own path is what positions map to, which is all this needs.
    /// </summary>
    private static Solution SyntheticVisualBasicSolution(string root, string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var document = DocumentInfo.Create(
            documentId,
            "Caller.vb",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default)),
            filePath: Path.Combine(root, "App", "Caller.vb"));

        var project = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "SyntheticVb",
            assemblyName: "SyntheticVb",
            language: LanguageNames.VisualBasic,
            filePath: Path.Combine(root, "App", "App.vbproj"),
            documents: new[] { document },
            metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) })
            .WithCompilationOptions(new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));

        var solution = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Default,
            filePath: Path.Combine(root, "SyntheticVb.sln"),
            projects: new[] { project });

        return workspace.AddSolution(solution);
    }

    /// <summary>
    /// A directory that exists on disk, and is removed afterwards. Most of these tests
    /// never touch the filesystem, but resolving the repository root is a walk up real
    /// directories looking for a real .git, so the tests for it need a real tree.
    /// </summary>
    internal sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vela-root-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    internal static string SyntheticRoot() =>
        Path.Combine(Path.GetTempPath(), "vela-synth-" + Guid.NewGuid().ToString("N")[..8]);

    internal static string Escape(string path) => path.Replace("\\", "\\\\");

    /// <summary>
    /// An in-memory solution holding one generated-looking C# file. Nothing is written
    /// to disk: the emitter only ever reads paths, never their contents.
    /// </summary>
    internal static Solution SyntheticSolution(string root, string source)
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
            metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) })
            // A library, not a console application. Without this the project defaults
            // to OutputKind.ConsoleApplication and every one of these fixtures carries
            // a CS5001 "no static Main" error that belongs to the fixture rather than
            // to the code under test.
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var solution = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Default,
            filePath: Path.Combine(root, "Synthetic.sln"),
            projects: new[] { project });

        return workspace.AddSolution(solution);
    }
}

/// <summary>
/// The package cache half of the classification: one of the two locations that can show
/// a file belongs to somebody else rather than being missing from the repository. These
/// tests set NUGET_PACKAGES, which is process-wide, so they share the non-parallel
/// collection with every other test that mutates the environment.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class ScipEmitterPackageCacheTests
{
    [Fact]
    public async Task EmitAsync_TreatsThePackageCacheAsExternalEvenWithNoRepositoryToCompareAgainst()
    {
        // Without a repository there is no boundary to be outside of, so every other
        // file one directory up stays a loud gap. The package cache is the exception
        // vela can name: NuGet owns it, nothing in it is the user's code, and a
        // restored package that contributes source (Microsoft.NET.Test.Sdk does) would
        // otherwise degrade every query from a perfectly complete index.
        using var cache = new ScipEmitterTests.TempDirectory();
        using var _ = new PackageCache(cache.Path);

        var root = ScipEmitterTests.SyntheticRoot();
        var external = Path.Combine(
            cache.Path, "microsoft.net.test.sdk", "18.4.0", "build", "net8.0",
            "Microsoft.NET.Test.Sdk.Program.cs");

        var solution = ScipEmitterTests.SyntheticSolution(root, $$"""
            #line 1 "{{ScipEmitterTests.Escape(external)}}"
            public class AutoGeneratedProgram { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("Microsoft.NET.Test.Sdk.Program.cs"));
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal));
        Assert.False(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
    }

    [Fact]
    public async Task EmitAsync_TreatsThePackageCacheAsExternalInsideARepositoryToo()
    {
        // The defect, in the shape it was found in: a 375,608 line solution at the root
        // of its repository, and one file from the NuGet package cache. SCIP cannot
        // hold it (every document must sit under project_root), so vela declined to
        // emit it and recorded the omission through the one channel it had, which
        // degrades the index. Nothing of the user's code was missing, and a banner that
        // fires on a stock .NET solution on every query forever teaches an agent to
        // ignore the one signal Constraint 3 depends on.
        using var cache = new ScipEmitterTests.TempDirectory();
        using var _ = new PackageCache(cache.Path);

        using var repository = new ScipEmitterTests.TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        var external = Path.Combine(
            cache.Path, "microsoft.net.test.sdk", "18.4.0", "build", "net8.0",
            "Microsoft.NET.Test.Sdk.Program.cs");

        var solution = ScipEmitterTests.SyntheticSolution(repository.Path, $$"""
            #line 1 "{{ScipEmitterTests.Escape(external)}}"
            public class AutoGeneratedProgram { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        // Still recorded, and still named: the file is genuinely not in the index, and
        // a reader asking why is owed the answer.
        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("Microsoft.NET.Test.Sdk.Program.cs"));

        // But not through the channel that means "code of yours is missing".
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal));

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());
        Assert.False(health.Degraded, health.Detail);
        Assert.Null(health.Detail);

        // Counted, so `vela index` can still say what it left out without a banner.
        Assert.Equal(1, Program.CountExternalDocuments(index));
    }

    [Fact]
    public async Task EmitAsync_FallsBackToTheDefaultPackageCacheWhenNugetPackagesIsUnset()
    {
        // NUGET_PACKAGES is usually unset, and NuGet's own default is
        // ~/.nuget/packages. Reading only the variable would leave the ordinary
        // machine, which is the one the defect was found on, unfixed.
        using var _ = new PackageCache(null);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile), "this test needs a user profile directory to exist");

        var root = ScipEmitterTests.SyntheticRoot();
        var external = Path.Combine(
            profile, ".nuget", "packages", "microsoft.net.test.sdk", "18.4.0", "build", "net8.0",
            "Microsoft.NET.Test.Sdk.Program.cs");

        var solution = ScipEmitterTests.SyntheticSolution(root, $$"""
            #line 1 "{{ScipEmitterTests.Escape(external)}}"
            public class AutoGeneratedProgram { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("Microsoft.NET.Test.Sdk.Program.cs"));
        Assert.False(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
    }

    [Fact]
    public async Task EmitAsync_TreatsAGlobalPackagesFolderFromNugetConfigAsExternal()
    {
        // The defect this closes. A repository that moves its package cache with
        // <add key="globalPackagesFolder" /> - the documented, supported way to do it -
        // had every file restored into that folder classified as a gap in its own code,
        // so the index was degraded on every build and every answer carried a false
        // INCOMPLETE banner. Nothing of the user's was missing.
        using var _ = new PackageCache(null);

        using var repository = new ScipEmitterTests.TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        // Outside the repository, because a folder INSIDE it is under project_root and
        // becomes an ordinary document without any of this being consulted.
        using var cache = new ScipEmitterTests.TempDirectory();
        File.WriteAllText(Path.Combine(repository.Path, "nuget.config"), $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{cache.Path}" /></config>
            </configuration>
            """);

        var external = Path.Combine(
            cache.Path, "microsoft.net.test.sdk", "18.4.0", "build", "net8.0",
            "Microsoft.NET.Test.Sdk.Program.cs");

        var solution = ScipEmitterTests.SyntheticSolution(repository.Path, $$"""
            #line 1 "{{ScipEmitterTests.Escape(external)}}"
            public class AutoGeneratedProgram { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("Microsoft.NET.Test.Sdk.Program.cs"));
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal));

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());
        Assert.False(health.Degraded, health.Detail);
    }

    [Fact]
    public async Task EmitAsync_TreatsAFallbackPackageFolderFromNugetConfigAsExternal()
    {
        // fallbackPackageFolders is the other half of the same setting and the one an
        // offline or air-gapped build depends on: packages are laid down once, read-only,
        // and every project reads them from there.
        using var _ = new PackageCache(null);

        using var repository = new ScipEmitterTests.TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        using var shared = new ScipEmitterTests.TempDirectory();
        File.WriteAllText(Path.Combine(repository.Path, "NuGet.Config"), $"""
            <configuration>
              <fallbackPackageFolders>
                <add key="Shared" value="{shared.Path}" />
              </fallbackPackageFolders>
            </configuration>
            """);

        var external = Path.Combine(shared.Path, "serilog", "4.0.0", "build", "Serilog.Generated.cs");

        var solution = ScipEmitterTests.SyntheticSolution(repository.Path, $$"""
            #line 1 "{{ScipEmitterTests.Escape(external)}}"
            public class GeneratedFromAFallbackFolder { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("Serilog.Generated.cs"));
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal));
        Assert.False(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
    }

    [Fact]
    public async Task EmitAsync_StillCallsFirstPartyCodeOutsideTheRepositoryAGap()
    {
        // The guard on the widening above. Reading nuget.config must not turn "outside the
        // repository" into "somebody else's": a linked file shared between two
        // repositories is the user's own code, absent from the index, and under
        // Constraint 3 it has to stay loud.
        using var _ = new PackageCache(null);

        using var repository = new ScipEmitterTests.TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        using var cache = new ScipEmitterTests.TempDirectory();
        File.WriteAllText(Path.Combine(repository.Path, "nuget.config"), $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{cache.Path}" /></config>
            </configuration>
            """);

        using var elsewhere = new ScipEmitterTests.TempDirectory();
        var shared = Path.Combine(elsewhere.Path, "Shared", "Thing.cs");

        var solution = ScipEmitterTests.SyntheticSolution(repository.Path, $$"""
            #line 1 "{{ScipEmitterTests.Escape(shared)}}"
            public class Thing { }
            #line default
            """);

        var emitted = await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default);
        var index = emitted.Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal) && a.Contains("Thing.cs"));
        Assert.True(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
    }

    /// <summary>Points NUGET_PACKAGES somewhere disposable, and puts it back.</summary>
    private sealed class PackageCache : IDisposable
    {
        private readonly string? _previous;

        public PackageCache(string? path)
        {
            _previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", path);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("NUGET_PACKAGES", _previous);
    }
}
