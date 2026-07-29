using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

        var index = (await ScipEmitter.EmitAsync(load.Solution, load.Failures, default)).Index;

        var razor = index.Documents
            .Where(d => d.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(fx.RazorFileCount, razor.Count);

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
    public async Task EmitAsync_MarksGeneratedDocumentsThatAreNotOnDisk_AndOnlyThose()
    {
        // The Razor generator does not write its output to disk unless
        // EmitCompilerGeneratedFiles is set, so the .g.cs documents in the index name
        // paths that do not exist. The originating .cshtml does exist and must never be
        // marked, or refs would suppress the very thing vela is for.
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var emitted = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        Assert.NotEmpty(emitted.GeneratedDocuments);

        foreach (var relativePath in emitted.GeneratedDocuments)
        {
            Assert.EndsWith(".g.cs", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(fx.Root, relativePath)),
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
    public async Task EmitAsync_RecordsEnclosingRangeOnDefinitions()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = (await ScipEmitter.EmitAsync(load.Solution, load.Failures, default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(load.Solution, load.Failures, default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

        // scip.proto forbids a relative_path that escapes project_root, so the file
        // cannot be emitted as a document.
        Assert.DoesNotContain(index.Documents, d => d.RelativePath.Contains(".."));
        Assert.DoesNotContain(index.Documents, d => Path.IsPathRooted(d.RelativePath));

        // Constraint 3: the omission has to be visible in the index it happened in.
        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.Contains("outside-project-root") && a.Contains("External.cshtml"));
    }

    [Fact]
    public async Task EmitAsync_RecordsAFileOutsideTheRepositoryAsExternalRatherThanAsAGapInIt()
    {
        // The defect, from a real 375,608 line solution. Microsoft.NET.Test.Sdk
        // contributes a generated entry point that lives in the NuGet package cache,
        // so SCIP cannot hold it (every document must sit under project_root) and vela
        // correctly declined to emit it. Recording that as degradation made every query
        // exit 3, permanently, on a stock .NET solution with nothing unusual about its
        // layout. Nothing of the user's code was missing. A banner that fires when
        // nothing is wrong teaches an agent to ignore the one signal Constraint 3
        // depends on, so the two cases are now separate channels.
        using var repository = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));

        using var elsewhere = new TempDirectory();
        var external = Path.Combine(elsewhere.Path, "microsoft.net.test.sdk", "18.4.0", "build", "Entry.cs");

        var solution = SyntheticSolution(repository.Path, $$"""
            #line 1 "{{Escape(external)}}"
            public class Entry { }
            #line default
            """);

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

        // Still recorded, and still named: the file is genuinely not in the index, and
        // a reader asking why is owed the answer.
        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal) && a.Contains("Entry.cs"));

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
    public async Task EmitAsync_KeepsAFileItCannotProveIsSomebodyElsesAsALoudGap()
    {
        // The other half of the split, and the half that must never soften. A solution
        // that is not in a repository gives vela nothing to measure "outside" against,
        // so a file it cannot place under project_root may well be the user's own code
        // sitting one directory up. Recording that as informational would hide a real
        // coverage gap, which is exactly the failure Constraint 3 exists to prevent.
        var root = SyntheticRoot();
        var outside = Path.Combine(SyntheticRoot(), "Lib", "External.cshtml");

        var solution = SyntheticSolution(root, $$"""
            #line 4 "{{Escape(outside)}}"
            public class Outsider { }
            #line default
            """);

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var method = Assert.Single(occurrences.Where(o =>
            o.Symbol == "Controller.Foo()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));

        // Mapped lines are zero-based and `#line 1` puts the first source line at 0, so
        // the attribute is line 2 and the declaration line 3, with `Foo` at column 15.
        Assert.Equal(3, method.Range[0]);
        Assert.Equal(15, method.Range[1]);

        // A type is anchored at its name for the same reason.
        var type = Assert.Single(occurrences.Where(o =>
            o.Symbol == "Controller" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;
        var occurrences = index.Documents.SelectMany(d => d.Occurrences).ToList();

        var references = occurrences
            .Where(o => o.Symbol == "Helper.DoIt()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0)
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
            o.Symbol == "Helper" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) == 0);

        // A VB declaration is two nodes, a block and the statement that opens it, and
        // GetDeclaredSymbol answers on both. Exactly one definition, at the identifier:
        // line 1, column 15 for `DoIt`, and line 0, column 14 for `Helper`.
        var definition = Assert.Single(occurrences.Where(o =>
            o.Symbol == "Helper.DoIt()" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));
        Assert.Equal(1, definition.Range[0]);
        Assert.Equal(15, definition.Range[1]);

        var module = Assert.Single(occurrences.Where(o =>
            o.Symbol == "Helper" && (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0));
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
/// The package cache half of the classification, which is the only thing that can tell
/// a solution outside any repository that a file belongs to somebody else. These tests
/// set NUGET_PACKAGES, which is process-wide, so they share the non-parallel collection
/// with every other test that mutates the environment.
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("Microsoft.NET.Test.Sdk.Program.cs"));
        Assert.DoesNotContain(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("outside-project-root:", StringComparison.Ordinal));
        Assert.False(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
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

        var index = (await ScipEmitter.EmitAsync(solution, Array.Empty<string>(), default)).Index;

        Assert.Contains(index.Metadata.ToolInfo.Arguments,
            a => a.StartsWith("external-document:", StringComparison.Ordinal)
                 && a.Contains("Microsoft.NET.Test.Sdk.Program.cs"));
        Assert.False(Program.BuildHealthRecord(index, Array.Empty<string>()).Degraded);
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
