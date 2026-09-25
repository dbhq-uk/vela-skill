using System.CommandLine;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// Which solution a verb means when --solution is not given.
///
/// Two defects, both met on a fresh .NET 10 project. Discovery looked for *.sln in the
/// current directory only, and `dotnet new sln` writes a .slnx on current SDKs, so a
/// project scaffolded the ordinary way failed `vela index` with "No single .sln found".
/// And only index and import read vela.json, so a repository that named its solution there
/// could build an index with a bare `vela index` and then not query it.
///
/// Every test here changes the current directory, which is process-wide, and resolves an
/// index through VELA_CACHE_HOME, so the class shares the non-parallel collection.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class SolutionDiscoveryTests
{
    [Fact]
    public async Task Index_WithNoSolutionArgument_FindsTheOnlySlnxInTheCurrentDirectory()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        var slnx = ReplaceSlnWithSlnx(fx);

        var indexed = await InDirectory(fx.Root, "index");

        Assert.Equal(0, indexed.ExitCode);
        Assert.DoesNotContain("No .sln or .slnx found", indexed.Output, StringComparison.Ordinal);

        // And the query verbs find the same solution, so they open the index just built.
        var def = await InDirectory(fx.Root, "def", "Thing.Value");
        Assert.Equal(0, def.ExitCode);
        Assert.Contains("Solo/Thing.cs", def.Output.Replace('\\', '/'), StringComparison.Ordinal);

        // The .slnx really is the one indexed: it is the only solution file there is.
        Assert.False(File.Exists(fx.SolutionPath));
        Assert.True(File.Exists(slnx));
    }

    [Fact]
    public async Task Index_FromASubdirectory_FindsTheSolutionAtTheRepositoryRoot()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        Directory.CreateDirectory(Path.Combine(fx.Root, ".git"));
        var below = Path.Combine(fx.Root, "Solo");

        var indexed = await InDirectory(below, "index");
        Assert.Equal(0, indexed.ExitCode);

        var refs = await InDirectory(below, "refs", "Thing.Value");
        Assert.Equal(0, refs.ExitCode);
        Assert.Contains("Solo/Thing.cs", refs.Output.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_StopsAtTheRepositoryRoot()
    {
        // A solution above the repository is somebody else's. Indexing it from inside the
        // repository would build an index of the wrong thing and say nothing about it.
        using var cache = new TempCacheHome();
        using var outer = new TempDirectory();
        File.WriteAllText(Path.Combine(outer.Path, "Outside.sln"), "");
        var repository = Path.Combine(outer.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var below = Path.Combine(repository, "src");
        Directory.CreateDirectory(below);

        var result = await InDirectory(below, "index");

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("No .sln or .slnx found", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Outside.sln", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_WithTwoSolutionsInOneDirectory_NamesBothAndPicksNeither()
    {
        // The migration case: `dotnet sln migrate` writes App.slnx beside App.sln. Either
        // could be meant, so vela says which it found rather than guessing.
        using var cache = new TempCacheHome();
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "App.sln"), "");
        File.WriteAllText(Path.Combine(dir.Path, "App.slnx"), "<Solution />");

        foreach (var verb in new[] { "index", "refs" })
        {
            var args = verb == "index" ? new[] { "index" } : new[] { "refs", "Anything" };
            var result = await InDirectory(dir.Path, args);

            Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
            Assert.Contains("holds more than one solution (App.sln, App.slnx)", result.Output, StringComparison.Ordinal);
            Assert.Contains("--solution", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task QueryVerbs_WithNoSolutionArgument_UseTheSolutionVelaJsonNames()
    {
        // Two solutions at the root, so discovery alone cannot choose, and vela.json says
        // which one this repository means. index read that already; the query verbs did
        // not, and every one of them exited 1 from the directory the index was built in.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        File.WriteAllText(Path.Combine(fx.Root, "Other.sln"), "");
        File.WriteAllText(Path.Combine(fx.Root, "vela.json"), """
            { "version": 1, "solution": "Fixture.sln" }
            """);

        var indexed = await InDirectory(fx.Root, "index");
        Assert.Equal(0, indexed.ExitCode);

        var queries = new[]
        {
            new[] { "def", "Thing.Value" },
            new[] { "refs", "Thing.Value" },
            new[] { "impact", "Thing.Value" },
            new[] { "find", "Thing" },
            new[] { "outline", "Solo/Thing.cs" }
        };

        foreach (var query in queries)
        {
            var result = await InDirectory(fx.Root, query);
            Assert.True(result.ExitCode == 0, $"{query[0]} exited {result.ExitCode}:\n{result.Output}");
        }
    }

    [Fact]
    public async Task QueryVerb_WithASolutionArgument_IsNotRedirectedByVelaJson()
    {
        // An explicit --solution is the user's answer to the question vela.json answers
        // by default, so the file must not override it.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        File.WriteAllText(Path.Combine(fx.Root, "vela.json"), """
            { "version": 1, "solution": "NotThisOne.sln" }
            """);

        Assert.Equal(0, (await InDirectory(fx.Root, "index", "--solution", fx.SolutionPath)).ExitCode);

        var result = await InDirectory(fx.Root, "def", "Thing.Value", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("NotThisOne", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryWithNoSolution_ImportsAndAnswersWithoutASolutionArgument()
    {
        // A repository whose only languages arrive through vela import has no solution, and
        // the documentation told people to invent one: --solution whatever.sln on every
        // command. Now the index is keyed on the repository.
        using var cache = new TempCacheHome();
        using var repository = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git"));
        var web = Path.Combine(repository.Path, "web");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "site.ts"), "\n\n\nexport function greet() {}\n");
        File.SetLastWriteTimeUtc(Path.Combine(web, "site.ts"), DateTime.UtcNow.AddHours(-1));

        var scip = Path.Combine(repository.Path, "index.scip");
        File.WriteAllBytes(scip, Google.Protobuf.MessageExtensions.ToByteArray(
            ScipImportEndToEndTests.ForeignIndex(repository.Path, "web/site.ts", "greet")));

        // Before any import there is nothing to answer from, and the error says what to pass.
        var before = await InDirectory(web, "refs", "greet");
        Assert.Equal(Program.ExitCannotAnswer, before.ExitCode);
        Assert.Contains("No .sln or .slnx found", before.Output, StringComparison.Ordinal);

        var imported = await InDirectory(web, "import", scip);
        Assert.True(imported.ExitCode == 0, imported.Output);
        Assert.Contains("keyed on the repository", imported.Output, StringComparison.Ordinal);

        // From anywhere in the repository, with no --solution.
        foreach (var directory in new[] { repository.Path, web })
        {
            var refs = await InDirectory(directory, "refs", "greet");
            Assert.True(refs.ExitCode == 0, refs.Output);
            Assert.Contains("web/site.ts", refs.Output, StringComparison.Ordinal);
        }

        Assert.Equal(0, (await InDirectory(repository.Path, "outline", "web/site.ts")).ExitCode);

        // Importing again with --replace finds the same index.
        Assert.Equal(0, (await InDirectory(repository.Path, "import", "--replace", scip)).ExitCode);

        // The cache knows what the index is of, and does not take it for an orphan: the
        // made-up solution the documentation used to suggest looked deleted, so the next
        // `vela index` of anything else could have removed it.
        var listing = await InDirectory(repository.Path, "cache");
        Assert.Contains($"of the repository at {repository.Path} (no solution)", listing.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT THERE", listing.Output, StringComparison.Ordinal);

        // vela index has nothing to build here, and still says so.
        var index = await InDirectory(repository.Path, "index");
        Assert.Equal(Program.ExitCannotAnswer, index.ExitCode);
        Assert.Contains("vela import builds an index keyed on the repository", index.Output, StringComparison.Ordinal);
    }

    /// <summary>Swaps the fixture's .sln for a .slnx holding the same project, which is
    /// what `dotnet new sln` produces on an SDK 10 scaffold.</summary>
    private static string ReplaceSlnWithSlnx(FixtureSolution fx)
    {
        var slnx = Path.ChangeExtension(fx.SolutionPath, ".slnx");
        File.WriteAllText(slnx, """
            <Solution>
              <Project Path="Solo/Solo.csproj" />
            </Solution>
            """);
        File.Delete(fx.SolutionPath);
        return slnx;
    }

    private static async Task<(int ExitCode, string Output)> InDirectory(string directory, params string[] args)
    {
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(directory);
            return await InvokeAsync(args);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
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

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vela-discover-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(path);
            Path = Vela.Indexing.RealPath.Of(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
