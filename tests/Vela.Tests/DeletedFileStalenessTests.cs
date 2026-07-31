using System.CommandLine;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// The freshness check could only ever notice something NEWER than the index, because it
/// only stated files that were there. Delete a source file, or rename one, and every
/// reference to it kept answering at exit 0 with no banner: the index names a file that is
/// not on disk, which is the worst shape of wrong answer, because an agent handed a path
/// will try to open it.
///
/// A rename is a deletion plus an addition, and the addition is the half the mtime walk
/// cannot see either - moving a file keeps its modification time, so the new path is not
/// newer than the index. Noticing the deletion is therefore what notices the rename, and
/// the test below pins that by making the moved file OLDER than the index, so nothing but
/// the deletion check can be what fires.
///
/// Indexing through the CLI resolves the index path from XDG_CACHE_HOME, which is
/// process-wide, so this class shares the non-parallel collection with every other test
/// that touches it.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class DeletedFileStalenessTests
{
    [Fact]
    public async Task Refs_DegradesAndNamesTheFileWhenASourceFileHasBeenDeleted()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        // Clean before the deletion, so what follows cannot be blamed on anything else.
        var clean = await InvokeAsync("refs", "Lib.Upstream.Twice(System.Int32)", "--solution", fx.SolutionPath);
        Assert.Equal(0, clean.ExitCode);

        File.Delete(Path.Combine(fx.Root, "Leaf", "Standalone.cs"));

        var result = await InvokeAsync("refs", "Lib.Upstream.Twice(System.Int32)", "--solution", fx.SolutionPath);

        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("INCOMPLETE", result.Output);
        Assert.Contains("Leaf/Standalone.cs", result.Output);
    }

    [Fact]
    public async Task Refs_DegradesAndNamesTheOldPathWhenASourceFileHasBeenRenamed()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var from = Path.Combine(fx.Root, "Leaf", "Standalone.cs");
        var to = Path.Combine(fx.Root, "Leaf", "Renamed.cs");
        File.Move(from, to);

        // Backdated deliberately. A rename keeps the modification time, so the new path is
        // not newer than the index and the mtime walk has nothing to say about it. Pushing
        // the time further into the past removes even the possibility, so a banner here can
        // only have come from noticing that the old path has gone.
        File.SetLastWriteTimeUtc(to, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await InvokeAsync("refs", "Lib.Upstream.Twice(System.Int32)", "--solution", fx.SolutionPath);

        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("INCOMPLETE", result.Output);
        Assert.Contains("Leaf/Standalone.cs", result.Output);
    }

    [Fact]
    public async Task Refs_DegradesWhenARazorViewHasBeenDeleted()
    {
        // A view is not compiled and never reaches the compiler as a file, so it is
        // recorded as an additional input rather than a source one. It is also the half of
        // this tool nothing else can see, and a deleted view leaving every answer at exit 0
        // would be the same silence in the place it costs most.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        File.Delete(Path.Combine(fx.Root, "App", "Pages", "Privacy.cshtml"));

        var result = await InvokeAsync("refs", "ViewData", "--solution", fx.SolutionPath);

        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("Privacy.cshtml", result.Output);
    }

    [Fact]
    public async Task Refs_StaysCleanWhenNothingHasGone()
    {
        // The guard on all of the above. The check runs on every query, so a false positive
        // is a banner on every answer forever, which is the crying-wolf failure Constraint
        // 3 cuts both ways on. A real solution carries generated files under obj, files with
        // extensions vela does not index, and files two projects share, and none of them may
        // produce a word.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var result = await InvokeAsync("refs", "ViewData", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", result.Output);
    }

    [Fact]
    public async Task Refs_IsCleanAgainOnceTheIndexHasBeenRebuilt()
    {
        // The banner has to clear, or it is not a signal. Re-indexing is the fix the banner
        // names, and after it the file is no longer one the index was built from.
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);
        File.Delete(Path.Combine(fx.Root, "Leaf", "Standalone.cs"));

        Assert.Equal(IndexHealth.ExitDegraded,
            (await InvokeAsync("refs", "Lib.Upstream.Twice(System.Int32)", "--solution", fx.SolutionPath)).ExitCode);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var result = await InvokeAsync("refs", "Lib.Upstream.Twice(System.Int32)", "--solution", fx.SolutionPath);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", result.Output);
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
            _path = Path.Combine(Path.GetTempPath(), "vela-gone-" + Guid.NewGuid().ToString("N")[..8]);
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
