using System.CommandLine;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// The suite must never write to, or clear, a cache directory that belongs to the person
/// running it. VELA_CACHE_HOME outranks XDG_CACHE_HOME, and every test class used to
/// isolate itself with XDG_CACHE_HOME alone, so a VELA_CACHE_HOME set in the shell sent
/// every index the suite built into that one real directory, and `vela cache clear --all`
/// then emptied it.
///
/// These tests stand in for that shell: they set VELA_CACHE_HOME to a directory of their
/// own before <see cref="TempCacheHome"/> is created, and check that directory is exactly
/// as it was afterwards.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class TempCacheHomeTests
{
    [Fact]
    public async Task IndexAndClearAll_LeaveAVelaCacheHomeSetInTheShellUntouched()
    {
        using var fx = FixtureSolution.CreateLibrary();
        var outer = Path.Combine(Path.GetTempPath(), "vela-outer-" + Guid.NewGuid().ToString("N")[..8]);
        var outerIndexes = Path.Combine(outer, "vela");
        Directory.CreateDirectory(outerIndexes);
        var someoneElses = Path.Combine(outerIndexes, "Theirs-0123456789abcdef.db");
        File.WriteAllText(someoneElses, "an index this suite did not build");

        try
        {
            using (CacheEnvironment.Save().With(CacheEnvironment.VelaCacheHome, outer))
            {
                var before = Snapshot(outer);

                using (var cache = new TempCacheHome())
                {
                    Assert.StartsWith(
                        RealPath.Of(cache.Path),
                        IndexPaths.CacheDirectory(),
                        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

                    Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);
                    Assert.Equal(before, Snapshot(outer));

                    Assert.Equal(0, (await InvokeAsync("cache", "clear", "--all")).ExitCode);
                    Assert.Equal(before, Snapshot(outer));
                }

                Assert.Equal(outer, Environment.GetEnvironmentVariable(CacheEnvironment.VelaCacheHome));
                Assert.True(File.Exists(someoneElses), "the suite removed an index it did not build");
            }
        }
        finally
        {
            try { Directory.Delete(outer, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Fact]
    public void Dispose_PutsBothCacheVariablesBackAsTheyWere()
    {
        using (CacheEnvironment.Save()
                   .With(CacheEnvironment.VelaCacheHome, "/was/vela")
                   .With(CacheEnvironment.XdgCacheHome, "/was/xdg"))
        {
            string path;
            using (var cache = new TempCacheHome())
            {
                path = cache.Path;
                Assert.Equal(cache.Path, Environment.GetEnvironmentVariable(CacheEnvironment.VelaCacheHome));
            }

            Assert.Equal("/was/vela", Environment.GetEnvironmentVariable(CacheEnvironment.VelaCacheHome));
            Assert.Equal("/was/xdg", Environment.GetEnvironmentVariable(CacheEnvironment.XdgCacheHome));
            Assert.False(Directory.Exists(path));
        }
    }

    /// <summary>Every file under a directory, with its size and write time, in a stable order.</summary>
    private static string Snapshot(string root) =>
        string.Join('\n', Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? $"{Path.GetRelativePath(root, p)} {new FileInfo(p).Length} {File.GetLastWriteTimeUtc(p):O}"
                : Path.GetRelativePath(root, p) + Path.DirectorySeparatorChar));

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
}
