using System.CommandLine;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// An edit made while <c>vela index</c> runs has to leave the index stale.
///
/// Until 25 Sep 2026 the build time was read after the harvest. A file edited while the
/// harvest ran then had an mtime older than the recorded build time, so it counted as
/// fresh, and every query answered at exit 0 with no banner, possibly from the text as it
/// was before the edit. On a large solution the harvest takes minutes, so the window was
/// wide. The build time is now read before the workspace is loaded, and these tests edit a
/// file at the one point that matters: after the load, before anything is harvested.
///
/// Indexing through the CLI resolves the index path from XDG_CACHE_HOME, which is
/// process-wide, so this class shares the non-parallel collection with every other test
/// that touches it.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class EditDuringIndexTests
{
    private const string Query = "Lib.Upstream.Twice(System.Int32)";

    [Fact]
    public async Task Index_LeavesTheIndexStaleWhenAFileIsEditedDuringTheRun()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        var index = await IndexEditingDuringTheRunAsync(fx, "index", "--solution", fx.SolutionPath);
        Assert.Equal(0, index.ExitCode);

        var result = await InvokeAsync("refs", Query, "--solution", fx.SolutionPath);

        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("stale index", result.Output);
        Assert.Contains("Leaf/Standalone.cs", result.Output);
    }

    [Fact]
    public async Task IncrementalIndex_LeavesTheIndexStaleWhenAFileIsEditedDuringTheRun()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var index = await IndexEditingDuringTheRunAsync(
            fx, "index", "--incremental", "--solution", fx.SolutionPath);
        Assert.Equal(0, index.ExitCode);

        var result = await InvokeAsync("refs", Query, "--solution", fx.SolutionPath);

        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("stale index", result.Output);
        Assert.Contains("Leaf/Standalone.cs", result.Output);
    }

    [Fact]
    public async Task Index_StaysCleanWhenNothingIsEditedDuringTheRun()
    {
        // The guard on the two above. Reading the clock earlier must not make an index
        // stale on its own: the fixture's files were all written before the run started,
        // and a banner on every answer after every index is the crying-wolf failure.
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var result = await InvokeAsync("refs", Query, "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("stale index", result.Output);
    }

    /// <summary>
    /// Runs the given index command with a hook that edits Leaf/Standalone.cs after the
    /// workspace has loaded and before anything is harvested.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> IndexEditingDuringTheRunAsync(
        FixtureSolution fx, params string[] args)
    {
        var edited = Path.Combine(fx.Root, "Leaf", "Standalone.cs");
        var ran = false;

        Program.AfterWorkspaceLoadForTesting.Value = () =>
        {
            File.AppendAllText(edited, "\n// edited while vela index ran\n");
            ran = true;
            return Task.CompletedTask;
        };

        try
        {
            var result = await InvokeAsync(args);
            Assert.True(ran, "the edit hook never ran, so this test proved nothing");
            return result;
        }
        finally
        {
            Program.AfterWorkspaceLoadForTesting.Value = null;
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

    /// <summary>Points XDG_CACHE_HOME at a disposable directory, and puts it back.</summary>
    private sealed class TempCacheHome : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TempCacheHome()
        {
            _path = Path.Combine(Path.GetTempPath(), "vela-edit-" + Guid.NewGuid().ToString("N")[..8]);
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
