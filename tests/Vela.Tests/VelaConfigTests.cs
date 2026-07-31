using System.CommandLine;
using Google.Protobuf;
using Vela.Config;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// `vela.json`: which languages a repository wants indexed, which indexer produces each
/// one, and what is not source code at all.
///
/// The measurement behind the whole file, taken on the real repository vela is developed
/// against: a naive extension count reports 5,775 Python files, of which 5,695 are
/// vendored `venv` and `site-packages`. Any tool that counts by extension without
/// opinionated default excludes reports a language profile that is simply false. So the
/// defaults are the feature and the config is how a repository overrides them.
/// </summary>
public class VelaConfigTests
{
    // ---------------------------------------------------------------- glob semantics

    [Fact]
    public void APatternWithNoSlash_MatchesTheFileNameAtAnyDepth()
    {
        // git: "If there is no separator at the end or the beginning of the pattern,
        // ... the pattern may also match at any level below".
        var filter = PathFilter.For(new[] { "*.min.js" });

        Assert.True(filter.Excludes("jquery.min.js"));
        Assert.True(filter.Excludes("src/Web/wwwroot/js/jquery.min.js"));
        Assert.False(filter.Excludes("src/Web/wwwroot/js/site.js"));
    }

    [Fact]
    public void APatternWithASlash_IsAnchoredToTheRoot()
    {
        var filter = PathFilter.For(new[] { "src/Web/wwwroot/app/**" });

        Assert.True(filter.Excludes("src/Web/wwwroot/app/main-4f2a1c.js"));

        // The same tail one directory deeper is a different file and is not excluded.
        Assert.False(filter.Excludes("vendor/src/Web/wwwroot/app/main-4f2a1c.js"));
    }

    [Fact]
    public void ALeadingDoubleStar_MatchesAtAnyDepthIncludingNone()
    {
        var filter = PathFilter.For(new[] { "**/site-packages/**" });

        Assert.True(filter.Excludes("site-packages/requests/api.py"));
        Assert.True(filter.Excludes("tools/venv/lib/python3.12/site-packages/requests/api.py"));
        Assert.False(filter.Excludes("tools/site_packages.py"));
    }

    [Fact]
    public void ASingleStar_DoesNotCrossADirectorySeparator()
    {
        // This is what scopes the ScentVerdict JavaScript job to wwwroot/js and nothing
        // under it, which is the entire point of that job's include list.
        var filter = PathFilter.For(new[] { "wwwroot/js/*.js" });

        Assert.True(filter.Excludes("wwwroot/js/site.js"));
        Assert.False(filter.Excludes("wwwroot/js/lib/site.js"));
    }

    [Fact]
    public void ADoubleStarInTheMiddle_MatchesZeroOrMoreDirectories()
    {
        var filter = PathFilter.For(new[] { "src/**/bin/**" });

        Assert.True(filter.Excludes("src/bin/App.dll"));
        Assert.True(filter.Excludes("src/App/Deep/bin/Debug/App.dll"));
        Assert.False(filter.Excludes("tests/App/bin/Debug/App.dll"));
    }

    [Fact]
    public void ATrailingSlash_MatchesADirectoryAndEverythingUnderIt()
    {
        var filter = PathFilter.For(new[] { "**/node_modules/" });

        Assert.True(filter.ExcludesDirectory("src/Web/node_modules"));
        Assert.True(filter.Excludes("src/Web/node_modules/lodash/index.js"));

        // Directory-only, so a FILE of that name is not excluded by it.
        Assert.False(filter.Excludes("src/Web/node_modules"));
    }

    [Fact]
    public void TheLastMatchingPatternDecides()
    {
        var filter = PathFilter.For(new[] { "**/*.js", "!wwwroot/js/site.js" });

        Assert.True(filter.Excludes("wwwroot/js/other.js"));
        Assert.False(filter.Excludes("wwwroot/js/site.js"));
    }

    [Fact]
    public void ANegationCannotReIncludeAFileWhoseParentDirectoryIsExcluded()
    {
        // git states this outright: "it is not possible to re-include a file if a parent
        // directory of that file is excluded". Following it rather than inventing
        // something friendlier is the whole reason to claim gitignore semantics: a
        // developer already knows this rule, and a tool that quietly differs from it will
        // exclude a file they believe they re-included.
        var filter = PathFilter.For(new[] { "**/node_modules/", "!**/node_modules/mine/keep.js" });

        Assert.True(filter.Excludes("src/node_modules/mine/keep.js"));
    }

    [Fact]
    public void ANegationTakesADefaultExcludeBack()
    {
        // The ctags `--languages=+C,-Java` idea, said in gitignore's syntax: a config's
        // exclude list ADDS to the defaults, and `!` is how a repository takes one back.
        var filter = PathFilter.For(VelaConfig.DefaultExcludes.Concat(new[] { "!**/dist/" }));

        Assert.True(filter.Excludes("src/Web/node_modules/lodash/index.js"));
        Assert.False(filter.Excludes("src/Web/dist/bundle.js"));
    }

    [Fact]
    public void MatchingIsCaseSensitiveAndSeparatorsAreNormalised()
    {
        var filter = PathFilter.For(new[] { "**/wwwroot/lib/**" });

        // A Windows path is the same path.
        Assert.True(filter.Excludes(@"src\Web\wwwroot\lib\jquery\jquery.js"));

        // git's globs are case-sensitive by default, and so are these.
        Assert.False(PathFilter.For(new[] { "*.JS" }).Excludes("site.js"));
    }

    [Fact]
    public void ACharacterClassMatchesOneOfItsMembers()
    {
        var filter = PathFilter.For(new[] { "**/*.[jt]sx" });

        Assert.True(filter.Excludes("src/App.jsx"));
        Assert.True(filter.Excludes("src/App.tsx"));
        Assert.False(filter.Excludes("src/App.vsx"));
    }

    // ---------------------------------------------------------------- the defaults

    [Fact]
    public void TheDefaultExcludes_KeepOutTheVendoredPythonThatMadeTheCountFalse()
    {
        var filter = PathFilter.For(VelaConfig.DefaultExcludes);

        // The 5,695 files.
        Assert.True(filter.Excludes("tools/venv/lib/python3.12/site-packages/requests/api.py"));
        Assert.True(filter.Excludes("tools/.venv/lib/python3.12/site-packages/requests/api.py"));

        // The 80 that are real.
        Assert.False(filter.Excludes("tools/importer/load.py"));

        // Build output, vendored client libraries and minified bundles.
        Assert.True(filter.Excludes("src/App/obj/Debug/net10.0/App.AssemblyInfo.cs"));
        Assert.True(filter.Excludes("src/App/bin/Release/net10.0/App.dll"));
        Assert.True(filter.Excludes("src/Web/node_modules/lodash/index.js"));
        Assert.True(filter.Excludes("src/Web/wwwroot/lib/jquery/dist/jquery.js"));
        Assert.True(filter.Excludes("src/Web/wwwroot/js/site.min.js"));

        // And first-party code is not excluded by any of them.
        Assert.False(filter.Excludes("src/Web/Pages/Index.cshtml"));
        Assert.False(filter.Excludes("src/Web/wwwroot/js/site.js"));
        Assert.False(filter.Excludes("src/Mobile/src/views/Home.vue"));
    }

    [Fact]
    public void WithNoConfigFile_TheConfigIsExactlyTodaysBehaviour()
    {
        using var tree = new TempTree();

        // Nothing to find, so nothing changes for a user who has never heard of the file.
        Assert.Null(VelaConfig.Find(tree.Root));

        var config = VelaConfig.ForDirectory(tree.Root);
        Assert.Null(config.FoundAt);
        Assert.Null(config.Solution);
        Assert.Equal(1, config.Version);

        // The two languages vela's own indexer has always produced, and nothing else.
        Assert.Equal(new[] { "csharp", "razor" }, config.Jobs.Select(j => j.Language));
        Assert.All(config.Jobs, j => Assert.Equal(IndexJob.VelaIndexer, j.Indexer));
        Assert.All(config.Jobs, j => Assert.Equal(".", j.Root));

        // No job anybody has to run, so nothing to be pending, so nothing to degrade.
        var plan = JobPlanner.For(config, tree.Root, _ => true);
        Assert.True(plan.RunsVelaIndexer);
        Assert.Empty(plan.Pending);
        Assert.Empty(plan.Problems);

        // And the default excludes are in force even with no file, because they are the
        // defaults rather than something the file switches on.
        Assert.Equal(VelaConfig.DefaultExcludes, config.Exclude);
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void Load_ReadsTheJobsArray()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""
            {
              "version": 1,
              "solution": "ScentVerdict.sln",
              "jobs": [
                { "language": "csharp",     "indexer": "vela",           "root": "." },
                { "language": "razor",      "indexer": "vela",           "root": "." },
                { "language": "typescript", "indexer": "scip-typescript", "root": "src/ScentVerdict.Mobile" },
                { "language": "javascript", "indexer": "scip-typescript", "root": "src/ScentVerdict.Web",
                  "include": ["wwwroot/js/**/*.js"] }
              ],
              "exclude": ["**/venv/**", "src/ScentVerdict.Web/wwwroot/app/**"]
            }
            """);

        var config = VelaConfig.ForDirectory(tree.Root);

        Assert.Equal(Path.Combine(tree.Root, "vela.json"), config.FoundAt);
        Assert.Equal(Path.Combine(tree.Root, "ScentVerdict.sln"), config.Solution);
        Assert.Equal(4, config.Jobs.Count);

        Assert.Equal(
            new[] { "csharp", "razor", "typescript", "javascript" },
            config.Jobs.Select(j => j.Language));
        Assert.Equal("scip-typescript", config.Jobs[2].Indexer);
        Assert.Equal("src/ScentVerdict.Mobile", config.Jobs[2].Root);
        Assert.Equal(new[] { "wwwroot/js/**/*.js" }, config.Jobs[3].Include);

        // Two jobs for one indexer at two roots, which is the reason this is a jobs array
        // and not a flat language list.
        Assert.Equal(2, config.Jobs.Count(j => j.Indexer == "scip-typescript"));

        // The config's excludes come after the defaults, so a default is still in force
        // and a config pattern can override one.
        Assert.Equal(VelaConfig.DefaultExcludes, config.Exclude.Take(VelaConfig.DefaultExcludes.Count));
        Assert.Equal(
            new[] { "**/venv/**", "src/ScentVerdict.Web/wwwroot/app/**" },
            config.Exclude.Skip(VelaConfig.DefaultExcludes.Count));
    }

    [Fact]
    public void Load_DefaultsTheIndexerToVelaAndTheRootToTheSolutionDirectory()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""{ "jobs": [ { "language": "csharp" } ] }""");

        var job = Assert.Single(VelaConfig.ForDirectory(tree.Root).Jobs);

        Assert.Equal(IndexJob.VelaIndexer, job.Indexer);
        Assert.Equal(".", job.Root);
        Assert.True(job.IsVela);
    }

    [Fact]
    public void Load_KeepsTheDefaultJobsWhenTheConfigOnlyOverridesTheExcludes()
    {
        // The commonest thing a repository actually wants: the .NET indexing it already
        // had, plus one directory of generated output kept out of the census.
        using var tree = new TempTree();
        tree.WriteConfig("""{ "exclude": ["src/Web/wwwroot/app/**"] }""");

        var config = VelaConfig.ForDirectory(tree.Root);

        Assert.Equal(new[] { "csharp", "razor" }, config.Jobs.Select(j => j.Language));
        Assert.Contains("src/Web/wwwroot/app/**", config.Exclude);
    }

    [Fact]
    public void Load_RefusesAVersionThisVelaDoesNotUnderstand()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""{ "version": 2, "jobs": [] }""");

        var problems = Problems(tree.Root);

        Assert.Contains(problems, p => p.Contains("version 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RefusesAFileItCannotParse()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""{ "jobs": [ }""");

        var problems = Problems(tree.Root);

        Assert.Contains(problems, p => p.Contains("vela.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RefusesAPropertyItDoesNotKnow()
    {
        // A misspelled key is the config failure with the worst consequence: `excludes`
        // for `exclude` silently indexes the vendored tree, and `jobbs` for `jobs`
        // silently loses a language. Both are exactly the quiet loss Constraint 3
        // forbids, so an unknown key is refused by name rather than ignored.
        using var tree = new TempTree();
        tree.WriteConfig("""{ "excludes": ["**/venv/**"] }""");

        var problems = Problems(tree.Root);

        Assert.Contains(problems, p => p.Contains("excludes", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("exclude", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_AllowsTheConventionalSchemaKey()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""{ "$schema": "https://example.invalid/vela.json", "jobs": [ { "language": "csharp" } ] }""");

        Assert.Single(VelaConfig.ForDirectory(tree.Root).Jobs);
    }

    [Fact]
    public void Load_RefusesAVelaJobForALanguageVelaCannotIndex()
    {
        // Constraint 1: language selection is explicit or defaulted, never guessed. A
        // config asking vela's own indexer for Python is asking for something that cannot
        // happen, and producing an index anyway would answer a question nobody asked.
        using var tree = new TempTree();
        tree.WriteConfig("""{ "jobs": [ { "language": "python", "indexer": "vela" } ] }""");

        var problems = Problems(tree.Root);

        Assert.Contains(problems, p => p.Contains("python", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("csharp", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RefusesAVelaJobRootedSomewhereOtherThanTheSolution()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""{ "jobs": [ { "language": "csharp", "root": "src/App" } ] }""");

        var problems = Problems(tree.Root);

        Assert.Contains(problems, p => p.Contains("src/App", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RefusesAJobWithNoLanguage()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""{ "jobs": [ { "indexer": "scip-go", "root": "tools" } ] }""");

        Assert.Contains(Problems(tree.Root), p => p.Contains("language", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RefusesTwoJobsThatCannotBeToldApart()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""
            {
              "jobs": [
                { "language": "typescript", "indexer": "scip-typescript", "root": "src/Mobile" },
                { "language": "typescript", "indexer": "scip-typescript", "root": "src/Mobile" }
              ]
            }
            """);

        Assert.Contains(Problems(tree.Root), p => p.Contains("twice", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_LooksInTheSolutionDirectoryAndUpToTheRepositoryRoot()
    {
        using var tree = new TempTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, ".git"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "src", "App"));
        tree.WriteConfig("""{ "jobs": [ { "language": "csharp" } ] }""");

        // A `repo/src/App.sln` layout is ordinary, so the file has to be found from
        // below as well as beside.
        Assert.Equal(
            Path.Combine(tree.Root, "vela.json"),
            VelaConfig.Find(Path.Combine(tree.Root, "src", "App")));
    }

    [Fact]
    public void Find_DoesNotClimbOutOfTheRepository()
    {
        // Otherwise one stray vela.json in a home directory silently reconfigures every
        // repository under it, and the index depends on where the clone happens to live.
        using var tree = new TempTree();
        tree.WriteConfig("""{ "jobs": [ { "language": "csharp" } ] }""");

        var inner = Path.Combine(tree.Root, "inner");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        Assert.Null(VelaConfig.Find(inner));
    }

    // ---------------------------------------------------------------- Constraint 3

    [Fact]
    public void AForeignJobIsPendingUntilItsIndexIsImported()
    {
        // The decision this test pins: vela does not run other indexers, so a job whose
        // indexer is not vela declares WHERE its .scip is expected to come from. `vela
        // index` cannot produce it, therefore the language is not in the index, therefore
        // the index is degraded and says which command would fix it. A config file must
        // never become a quiet way to lose a language.
        using var tree = new TempTree();
        tree.WriteConfig("""
            {
              "jobs": [
                { "language": "csharp" },
                { "language": "razor" },
                { "language": "typescript", "indexer": "scip-typescript", "root": "src/Mobile" }
              ]
            }
            """);
        Directory.CreateDirectory(Path.Combine(tree.Root, "src", "Mobile"));

        var plan = JobPlanner.For(VelaConfig.ForDirectory(tree.Root), tree.Root, _ => true);

        var pending = Assert.Single(plan.Pending);

        // Keyed by the absolute path of the .scip the job expects, which is exactly the
        // key `vela import` clears, so importing that file clears the job and nothing else
        // has to remember it.
        //
        // Proven by marking the file the job is talking about and reading the mark back
        // THROUGH the key. The expectation used to be computed with RealPath.Of, the
        // resolution this key is made of, so it agreed with whatever that resolution did,
        // including nothing at all, and no regression in it could fail this test. Reading
        // the file the key names asks the filesystem instead, and only one file in this
        // tree carries the mark. Written after the plan, so the job is still pending.
        var expected = Path.Combine(tree.Root, "src", "Mobile", "index.scip");
        var mark = Guid.NewGuid().ToString("N");
        File.WriteAllText(expected, mark);

        Assert.True(Path.IsPathRooted(pending.Source));
        Assert.EndsWith(Path.Combine("src", "Mobile", "index.scip"), pending.Source, StringComparison.Ordinal);
        Assert.Equal(mark, File.ReadAllText(pending.Source));
        Assert.Contains("typescript", pending.Detail, StringComparison.Ordinal);
        Assert.Contains("scip-typescript", pending.Detail, StringComparison.Ordinal);
        Assert.Contains("vela import", pending.Detail, StringComparison.Ordinal);

        // A vela job is still expected beside it: the C# does not stop being indexed
        // because a second language was declared.
        Assert.True(plan.RunsVelaIndexer);
    }

    [Fact]
    public void AForeignJobSaysSoWhenItsIndexerIsNotInstalled()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""
            { "jobs": [ { "language": "go", "indexer": "scip-go", "root": "tools" } ] }
            """);
        Directory.CreateDirectory(Path.Combine(tree.Root, "tools"));

        var plan = JobPlanner.For(VelaConfig.ForDirectory(tree.Root), tree.Root, _ => false);

        var pending = Assert.Single(plan.Pending);
        Assert.Contains("not found", pending.Detail, StringComparison.Ordinal);
        Assert.Contains("PATH", pending.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The example config the README points at is a shipped artefact, so it is held to
    /// what the README itself says.
    ///
    /// It wrote every exclude as `dir/**`, which git reads as "the files inside that
    /// directory" and not as the directory, so <see cref="PathFilter.ExcludesDirectory"/>
    /// says no and the walk descends into the subtree and rejects each file one at a time.
    /// Every other entry in it restates a default that IS written in the pruning form, so
    /// the walk pruned anyway - except `src/ScentVerdict.Web/wwwroot/app/`, which has no
    /// default behind it and is the very directory the motivation cites: gitignored build
    /// output of the mobile app holding 64 of the repository's 81 JavaScript files.
    /// </summary>
    [Fact]
    public void TheShippedExampleConfigPrunesTheDirectoriesItExcludes()
    {
        var path = RepositoryFile("docs/examples/scentverdict-vela.json");

        // It parses, which is the other thing a shipped example owes a reader.
        var config = VelaConfig.Load(path);
        var filter = PathFilter.For(config.Exclude);

        Assert.True(
            filter.ExcludesDirectory("src/ScentVerdict.Web/wwwroot/app"),
            "the example must let the walk prune the directory the README cites, not merely reject "
            + "its files one at a time");

        // And the files inside it are still excluded, which is the whole point of the two
        // forms being interchangeable in effect.
        Assert.True(filter.Excludes("src/ScentVerdict.Web/wwwroot/app/main-4f2a1c.js"));

        // No entry in it is written in the non-pruning form, so the example teaches the
        // form the README recommends rather than the one it warns about.
        Assert.DoesNotContain("/**\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>A file of this repository, found from the test binary by walking up to the
    /// solution, so the test does not depend on where it was run from.</summary>
    private static string RepositoryFile(string relativePath)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"{relativePath} was not found above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void AJobWhoseRootIsNotThereIsAProblemInItsOwnRight()
    {
        // A root that does not exist can never be satisfied by any import, so it is
        // reported as the config error it is rather than as work outstanding.
        using var tree = new TempTree();
        tree.WriteConfig("""
            { "jobs": [ { "language": "python", "indexer": "scip-python", "root": "tools/etl" } ] }
            """);

        var plan = JobPlanner.For(VelaConfig.ForDirectory(tree.Root), tree.Root, _ => true);

        Assert.Contains(plan.Problems, p => p.Contains("tools/etl", StringComparison.Ordinal));
        Assert.True(plan.Degraded);
    }

    [Fact]
    public void AVelaJobForCsharpAloneSaysThatRazorArrivesWithIt()
    {
        // vela's Razor documents come through the Razor source generator into the same
        // compilation as the C#, so they cannot be indexed apart from it. A config naming
        // one without the other gets what it asked for plus the other, and is told so:
        // an index holding a language nobody asked for is a smaller sin than a silent one.
        using var tree = new TempTree();
        tree.WriteConfig("""{ "jobs": [ { "language": "csharp" } ] }""");

        var plan = JobPlanner.For(VelaConfig.ForDirectory(tree.Root), tree.Root, _ => true);

        Assert.True(plan.RunsVelaIndexer);
        Assert.Contains(plan.Notes, n => n.Contains("razor", StringComparison.Ordinal));
        Assert.False(plan.Degraded);
    }

    [Fact]
    public void AConfigWithNoVelaJobLeavesNothingForVelaIndexToBuild()
    {
        using var tree = new TempTree();
        tree.WriteConfig("""
            { "jobs": [ { "language": "typescript", "indexer": "scip-typescript", "root": "." } ] }
            """);

        var plan = JobPlanner.For(VelaConfig.ForDirectory(tree.Root), tree.Root, _ => true);

        Assert.False(plan.RunsVelaIndexer);
    }

    // ---------------------------------------------------------------- the census

    [Fact]
    public void TheCensus_CountsFirstPartyFilesAndNotVendoredOnes()
    {
        // In miniature, the measurement this whole file exists for.
        using var tree = new TempTree();
        tree.WriteFile("tools/etl/load.py", "print(1)");
        tree.WriteFile("tools/etl/transform.py", "print(2)");
        for (var i = 0; i < 20; i++)
            tree.WriteFile($"tools/venv/lib/python3.12/site-packages/dep{i}/api.py", "pass");
        tree.WriteFile("db/schema.sql", "select 1");
        tree.WriteFile("src/App/Program.cs", "class C {}");
        tree.WriteFile("src/App/obj/Debug/App.g.cs", "class G {}");

        var census = LanguageCensus.Take(tree.Root, PathFilter.For(VelaConfig.DefaultExcludes));

        Assert.Equal(2, census.Count("python"));
        Assert.Equal(1, census.Count("sql"));
        Assert.Equal(1, census.Count("csharp"));

        // The vendored tree was pruned rather than walked file by file, which is the
        // difference between a walk that costs nothing and one that reads 5,695 files.
        Assert.True(census.PrunedDirectories > 0);
    }

    [Fact]
    public void TheCensus_NamesTheLanguagesNoJobCovers()
    {
        using var tree = new TempTree();
        tree.WriteFile("src/App/Program.cs", "class C {}");
        tree.WriteFile("tools/etl/load.py", "print(1)");
        tree.WriteFile("db/schema.sql", "select 1");

        var config = VelaConfig.ForDirectory(tree.Root);
        var census = LanguageCensus.Take(tree.Root, PathFilter.For(config.Exclude));
        var uncovered = census.NotCoveredBy(config.Jobs);

        // csharp has a job by default; python and sql have none, most numerous first,
        // then by name, so the same tree renders the same sentence every time.
        Assert.Equal(new[] { "python", "sql" }, uncovered.Select(l => l.Language));
        Assert.DoesNotContain("csharp", uncovered.Select(l => l.Language));
    }

    private static IReadOnlyList<string> Problems(string root)
    {
        var thrown = Assert.Throws<VelaConfigException>(() => VelaConfig.ForDirectory(root));
        Assert.NotEmpty(thrown.Problems);
        return thrown.Problems;
    }

    /// <summary>A disposable directory to put a vela.json and a few files in.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; }

        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "vela-cfg-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        public void WriteConfig(string json) => File.WriteAllText(Path.Combine(Root, "vela.json"), json);

        public void WriteFile(string relativePath, string contents)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}

/// <summary>
/// The config through the real command, on a real solution vela really indexes.
///
/// Indexing through the CLI resolves the index path from XDG_CACHE_HOME, which is
/// process-wide, so this class shares the non-parallel collection with every other test
/// that touches it.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class VelaConfigEndToEndTests
{
    [Fact]
    public async Task Index_WithNoConfigFile_IsExactlyWhatItWasBefore()
    {
        // The requirement that outranks the feature: an existing user must see no change
        // whatever. No file, no extra line of output, no mention of vela.json, exit 0.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var result = await InvokeAsync("index", "--stats", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("vela.json", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("INCOMPLETE", result.Output, StringComparison.Ordinal);

        // And the coverage that must not regress: one document per Razor view.
        Assert.True(fx.RazorFileCount > 0, "fixture must contain .cshtml files");
        Assert.Contains($"razor views        : {fx.RazorFileCount}", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_WithAConfiguredJobItCannotRun_DegradesUntilThatJobIsImported()
    {
        // The whole of Constraint 3 for this feature, through the CLI, on one index:
        // declare a second language, watch `vela index` refuse to call the result
        // complete, watch every query inherit the banner, then satisfy the job and watch
        // it clear.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var mobile = Path.Combine(fx.Root, "src", "Mobile");
        Directory.CreateDirectory(mobile);
        File.WriteAllText(Path.Combine(fx.Root, "vela.json"), """
            {
              "version": 1,
              "jobs": [
                { "language": "csharp", "indexer": "vela", "root": "." },
                { "language": "razor",  "indexer": "vela", "root": "." },
                { "language": "typescript", "indexer": "scip-typescript", "root": "src/Mobile" }
              ]
            }
            """);

        var indexed = await InvokeAsync("index", "--solution", fx.SolutionPath);

        Assert.Equal(IndexHealth.ExitDegraded, indexed.ExitCode);
        Assert.Contains("vela.json", indexed.Output, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", indexed.Output, StringComparison.Ordinal);
        Assert.Contains("typescript", indexed.Output, StringComparison.Ordinal);
        Assert.Contains("vela import", indexed.Output, StringComparison.Ordinal);

        // The C# is in there and answers, and the answer carries the banner and the exit
        // code, because the index really is missing a language somebody asked for.
        var refs = await InvokeAsync("refs", "ViewData", "--solution", fx.SolutionPath);
        Assert.Equal(IndexHealth.ExitDegraded, refs.ExitCode);
        Assert.Contains("INCOMPLETE", refs.Output, StringComparison.Ordinal);
        Assert.Contains(".cshtml", refs.Output, StringComparison.Ordinal);

        // The indexer is run, by whatever runs it, and its output lands where the job
        // said it would.
        var scip = Path.Combine(mobile, "index.scip");
        File.WriteAllBytes(scip, ForeignIndex(fx.Root, "src/Mobile/app.ts", "greet").ToByteArray());

        var imported = await InvokeAsync("import", scip, "--solution", fx.SolutionPath);
        Assert.Equal(0, imported.ExitCode);

        // The job is satisfied, so the banner is gone and both languages answer from one
        // index.
        var clean = await InvokeAsync("refs", "greet", "--solution", fx.SolutionPath);
        Assert.Equal(0, clean.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", clean.Output, StringComparison.Ordinal);
        Assert.Contains("app.ts", clean.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A job has to settle wherever it is imported from.
    ///
    /// The pending row is keyed on the .scip resolved against the REPOSITORY ROOT, and the
    /// import used to clear it on the path resolved against the CURRENT WORKING DIRECTORY.
    /// The two agree for a path typed relative to the directory the user is standing in,
    /// and they do not agree for the command `vela index` itself prints, which names the
    /// .scip relative to the repository: run that from a subdirectory and the file is not
    /// found, so nothing is imported, the job stays pending forever, and every answer
    /// prints INCOMPLETE naming a command the user has already run. That is the
    /// crying-wolf failure this project has paid for twice.
    ///
    /// So all three forms are asserted: the absolute path, the path relative to the
    /// directory the user is in, and the repository-relative path vela printed.
    /// </summary>
    [Theory]
    [InlineData(Form.Absolute)]
    [InlineData(Form.RelativeToCurrentDirectory)]
    [InlineData(Form.RelativeToRepositoryRoot)]
    public async Task Import_SettlesTheJobWhicheverWayItsPathIsWritten(Form form)
    {
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var mobile = Path.Combine(fx.Root, "src", "Mobile");
        Directory.CreateDirectory(mobile);
        File.WriteAllText(Path.Combine(fx.Root, "vela.json"), """
            {
              "version": 1,
              "jobs": [
                { "language": "csharp", "indexer": "vela", "root": "." },
                { "language": "razor",  "indexer": "vela", "root": "." },
                { "language": "typescript", "indexer": "scip-typescript", "root": "src/Mobile" }
              ]
            }
            """);

        var indexed = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(IndexHealth.ExitDegraded, indexed.ExitCode);

        // The command the verb printed, verbatim, is the third form below.
        Assert.Contains("vela import src/Mobile/index.scip", indexed.Output, StringComparison.Ordinal);

        var scip = Path.Combine(mobile, "index.scip");
        File.WriteAllBytes(scip, ForeignIndex(fx.Root, "src/Mobile/app.ts", "greet").ToByteArray());

        // Every one of these names the same file. The user stands in src/, which is where
        // anybody who has just run the indexer over src/Mobile is standing.
        var (argument, from) = form switch
        {
            Form.Absolute => (scip, Path.Combine(fx.Root, "src")),
            Form.RelativeToCurrentDirectory => ("Mobile/index.scip", Path.Combine(fx.Root, "src")),
            _ => ("src/Mobile/index.scip", Path.Combine(fx.Root, "src"))
        };

        var previous = Directory.GetCurrentDirectory();
        (int ExitCode, string Output) imported;
        try
        {
            Directory.SetCurrentDirectory(from);
            imported = await InvokeAsync("import", argument, "--solution", fx.SolutionPath);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.Equal(0, imported.ExitCode);

        // The job is settled, so no answer carries the banner any more.
        var clean = await InvokeAsync("refs", "greet", "--solution", fx.SolutionPath);
        Assert.Equal(0, clean.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", clean.Output, StringComparison.Ordinal);
        Assert.Contains("app.ts", clean.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it has to settle when the repository is reached through a symbolic link, which
    /// is the same identity failure arriving by a different road.
    ///
    /// This is a product bug rather than a platform quirk, which is why the test is here
    /// and not skipped on Linux. Path.GetFullPath removes '.', '..' and a relative prefix
    /// and stops: it does not resolve links. So the pending row went in under the link's
    /// spelling while the import cleared it under the target's, and the job could never be
    /// settled - every answer printing INCOMPLETE for an import that had already been run.
    ///
    /// macOS hits it with no symbolic link of the user's own, because Path.GetTempPath
    /// there is under /var and /var is a link to /private/var, which is how CI found it.
    /// The link made here is the same shape and reproduces it on any platform, so the
    /// assertion is being made where it is cheapest to run as well as where it was found.
    /// </summary>
    [SymbolicLinkFact]
    public async Task Import_SettlesTheJobWhenTheRepositoryIsReachedThroughASymbolicLink()
    {
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var mobile = Path.Combine(fx.Root, "src", "Mobile");
        Directory.CreateDirectory(mobile);
        File.WriteAllText(Path.Combine(fx.Root, "vela.json"), """
            {
              "version": 1,
              "jobs": [
                { "language": "csharp", "indexer": "vela", "root": "." },
                { "language": "razor",  "indexer": "vela", "root": "." },
                { "language": "typescript", "indexer": "scip-typescript", "root": "src/Mobile" }
              ]
            }
            """);

        // The repository, named the way a user with a symlinked checkout names it. Every
        // path below goes through the link and never through the target.
        var link = Path.Combine(Path.GetTempPath(), "vela-link-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateSymbolicLink(link, fx.Root);

        try
        {
            var solution = Path.Combine(link, Path.GetFileName(fx.SolutionPath));

            var indexed = await InvokeAsync("index", "--solution", solution);
            Assert.Equal(IndexHealth.ExitDegraded, indexed.ExitCode);

            var scip = Path.Combine(link, "src", "Mobile", "index.scip");
            File.WriteAllBytes(scip, ForeignIndex(fx.Root, "src/Mobile/app.ts", "greet").ToByteArray());

            var previous = Directory.GetCurrentDirectory();
            (int ExitCode, string Output) imported;
            try
            {
                // Standing inside the link, which is where the user who ran the indexer
                // is standing, and naming the file the way `vela index` printed it.
                Directory.SetCurrentDirectory(Path.Combine(link, "src"));
                imported = await InvokeAsync("import", "src/Mobile/index.scip", "--solution", solution);
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }

            Assert.Equal(0, imported.ExitCode);

            var clean = await InvokeAsync("refs", "greet", "--solution", solution);
            Assert.Equal(0, clean.ExitCode);
            Assert.DoesNotContain("INCOMPLETE", clean.Output, StringComparison.Ordinal);
            Assert.Contains("app.ts", clean.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>The three ways a reader can honestly write the path of a .scip that sits
    /// in the repository they are standing in.</summary>
    public enum Form
    {
        Absolute,
        RelativeToCurrentDirectory,
        RelativeToRepositoryRoot
    }

    [Fact]
    public async Task Index_WithAConfigItCannotUnderstand_ChangesNothingAndSaysWhy()
    {
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        File.WriteAllText(Path.Combine(fx.Root, "vela.json"), """{ "jobbs": [] }""");

        var result = await InvokeAsync("index", "--solution", fx.SolutionPath);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("jobbs", result.Output, StringComparison.Ordinal);

        // The index it was pointed at is exactly as it was, so the C# still answers: a
        // config vela cannot read must not also cost the index that was already built.
        var refs = await InvokeAsync("refs", "ViewData", "--solution", fx.SolutionPath);
        Assert.Equal(0, refs.ExitCode);
        Assert.Contains(".cshtml", refs.Output, StringComparison.Ordinal);
    }

    private static Scip.Index ForeignIndex(string root, string relativePath, params string[] names)
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                ProjectRoot = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri,
                ToolInfo = new Scip.ToolInfo { Name = "scip-typescript", Version = "0.4.0" }
            }
        };

        var doc = new Scip.Document { RelativePath = relativePath, Language = "typescript" };
        for (var i = 0; i < names.Length; i++)
        {
            doc.Occurrences.Add(new Scip.Occurrence
            {
                Symbol = $"scip-typescript npm app 1.0.0 `app.ts`/{names[i]}().",
                SymbolRoles = (int)Scip.SymbolRole.Definition,
                SingleLineRange = new Scip.SingleLineRange { Line = 3 + i, StartCharacter = 9, EndCharacter = 14 }
            });
        }

        index.Documents.Add(doc);
        return index;
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        using var writer = new StringWriter();
        var configuration = new InvocationConfiguration
        {
            Output = writer,
            Error = writer,
            EnableDefaultExceptionHandler = false
        };

        var exitCode = await Program.BuildRootCommand().Parse(args).InvokeAsync(configuration);
        return (exitCode, writer.ToString());
    }

    /// <summary>Points XDG_CACHE_HOME at a disposable directory, and puts it back.</summary>
    private sealed class TempCacheHome : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TempCacheHome()
        {
            _path = Path.Combine(Path.GetTempPath(), "vela-cfg-e2e-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_path);
            _previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _previous);
            try { Directory.Delete(_path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
