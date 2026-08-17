using System.CommandLine;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// A solution vela cannot open is a sentence and an exit code, not a stack trace.
///
/// `vela index --solution NoSuchThing.sln` used to walk straight past the load failure it
/// had already recorded, hand Roslyn's empty fallback solution to the emitter, and die on
/// `Path.GetDirectoryName(solution.FilePath)!` with an ArgumentNullException and eight
/// frames of trace. The diagnosis was sitting in <see cref="Vela.Harvest.LoadResult.Failures"/>
/// the whole time and was thrown away.
///
/// The exit code was the worse half. A mistyped path, or a solution somebody moved, is the
/// single most likely way to run this verb wrongly, and an agent reading the exit code was
/// told the index had been built. Constraint 3 forbids exactly that: an index that does not
/// exist must never look like one that does.
///
/// These tests drive the real command. They share the non-parallel collection with every
/// other test that resolves an index through XDG_CACHE_HOME.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class MissingSolutionTests
{
    [Fact]
    public async Task Index_OnASolutionThatIsNotThere_SaysSoAndExitsCannotAnswer()
    {
        using var cache = new TempCacheHome();
        using var dir = new TempDirectory();

        var missing = Path.Combine(dir.Path, "NoSuchThing.sln");
        var result = await InvokeAsync("index", "--solution", missing);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);

        // Said, and said about the path that was asked for. A message that does not name
        // the path cannot be acted on when the path came out of a script.
        Assert.Contains("could not open the solution", result.Output, StringComparison.Ordinal);
        Assert.Contains(missing, result.Output, StringComparison.Ordinal);

        // The reason WorkspaceLoader already recorded, not a fresh sentence guessing at it.
        Assert.Contains("NoSuchThing.sln", result.Output, StringComparison.Ordinal);

        // No trace, and no claim that anything was indexed.
        Assert.DoesNotContain("Unhandled exception", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Indexed ", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_OnADirectory_SaysItIsADirectoryAndExitsCannotAnswer()
    {
        using var cache = new TempCacheHome();
        using var dir = new TempDirectory();

        var result = await InvokeAsync("index", "--solution", dir.Path);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("could not open the solution", result.Output, StringComparison.Ordinal);
        Assert.Contains(dir.Path, result.Output, StringComparison.Ordinal);

        // A directory is the one wrong path a user is most likely to have typed on purpose,
        // so it is worth telling them what --solution actually takes.
        Assert.Contains("directory", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Unhandled exception", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Indexed ", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_OnAProjectFile_SaysItIsAProjectAndExitsCannotAnswer()
    {
        using var cache = new TempCacheHome();
        using var dir = new TempDirectory();

        var csproj = Path.Combine(dir.Path, "App.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var result = await InvokeAsync("index", "--solution", csproj);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("could not open the solution", result.Output, StringComparison.Ordinal);
        Assert.Contains(csproj, result.Output, StringComparison.Ordinal);

        // vela indexes a solution, so the fix is to name the .sln that includes this
        // project rather than to retry with a different spelling of the same mistake.
        Assert.Contains("project file", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Unhandled exception", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Indexed ", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_OnASlnWhoseSlnxIsTheOneOnDisk_PointsAtTheSlnx()
    {
        // `dotnet new sln` emits a .slnx by default on current SDKs, and the muscle memory
        // of every .NET developer alive types .sln. The file is sitting right there, so
        // saying "not found" and stopping would be a worse answer than the one line it
        // takes to point at it.
        using var cache = new TempCacheHome();
        using var dir = new TempDirectory();

        var slnx = Path.Combine(dir.Path, "App.slnx");
        File.WriteAllText(slnx, "<Solution />");

        var result = await InvokeAsync("index", "--solution", Path.Combine(dir.Path, "App.sln"));

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains(slnx, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_OnAFileThatIsNotASolution_SaysSoAndExitsCannotAnswer()
    {
        // The .sln extension is not the test; whether a solution came out of it is. A file
        // named .sln that holds something else reaches the loader, which is where the true
        // reason comes from, so nothing here guesses at what that reason will be.
        using var cache = new TempCacheHome();
        using var dir = new TempDirectory();

        var rubbish = Path.Combine(dir.Path, "NotReally.sln");
        File.WriteAllText(rubbish, "this is not a solution file at all");

        var result = await InvokeAsync("index", "--solution", rubbish);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("could not open the solution", result.Output, StringComparison.Ordinal);
        Assert.Contains(rubbish, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Unhandled exception", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Indexed ", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_OnASolutionThatOpensWithNoProjects_StillDegradesRatherThanRefusing()
    {
        // The neighbour on the other side of the new guard, and the one it must not eat. A
        // solution with no projects in it OPENS: it has a FilePath, it has a directory to be
        // rooted at, and it builds an index that is empty and says so at exit 3. Refusing it
        // at exit 1 would be a regression dressed as a fix.
        using var cache = new TempCacheHome();
        using var fx = FixtureSolution.CreateEmptySolution();

        var result = await InvokeAsync("index", "--solution", fx.SolutionPath);

        Assert.DoesNotContain("could not open the solution", result.Output, StringComparison.Ordinal);
        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("INCOMPLETE", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_WithNoSolutionArgumentAndNoSolutionAround_KeepsItsOwnMessage()
    {
        // The oldest of these messages, and the one a user meets by running `vela index` in
        // the wrong directory. It is settled before anything is loaded, so it must not be
        // replaced by the load-failure sentence above.
        using var cache = new TempCacheHome();
        using var dir = new TempDirectory();

        var previous = Directory.GetCurrentDirectory();
        (int ExitCode, string Output) result;
        try
        {
            Directory.SetCurrentDirectory(dir.Path);
            result = await InvokeAsync("index");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains(
            "No single .sln found in the current directory. Pass --solution <path to the .sln>.",
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("could not open the solution", result.Output, StringComparison.Ordinal);
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

    /// <summary>An empty directory of its own, removed afterwards.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vela-missing-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);

            // Resolved, because every path vela reports is resolved and macOS reaches the
            // temp directory through /var, a link to /private/var. Comparing an unresolved
            // fixture path against a resolved message would fail there and nowhere else.
            Path = Vela.Indexing.RealPath.Of(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    /// <summary>Points XDG_CACHE_HOME at a disposable directory, and puts it back.</summary>
    private sealed class TempCacheHome : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TempCacheHome()
        {
            _path = Path.Combine(Path.GetTempPath(), "vela-missing-cache-" + Guid.NewGuid().ToString("N")[..8]);
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
