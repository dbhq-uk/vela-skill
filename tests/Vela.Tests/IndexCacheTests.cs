using System.CommandLine;
using System.Runtime.Versioning;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// Nothing had ever removed an index. `~/.cache/vela` on the development machine stood at
/// 983MB across five databases, and it could only grow: index a few solutions, or one
/// solution under two spellings, and a user who never thinks about the cache eventually
/// notices their disk.
///
/// The policy these tests pin is deliberately conservative, and every part of it is either
/// asked for or announced:
///
///   - a `vela cache` verb, which lists what is held and clears what the user names;
///   - during `vela index` only, an index whose SOLUTION IS GONE is removed, because it
///     cannot be one anybody was about to use;
///   - during `vela index` only, and only above a total-size budget, the least recently
///     built indexes are removed until the cache is under it - never the one this run just
///     wrote, and never one built within the last week.
///
/// Nothing is ever removed by a query. A query is read-only and stays read-only, and the
/// moment somebody runs one is the moment they are most likely to be about to use another
/// index.
///
/// Indexing through the CLI resolves the cache directory from XDG_CACHE_HOME, which is
/// process-wide, so this class shares the non-parallel collection with every other test
/// that touches it.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class IndexCacheTests
{
    [Fact]
    public async Task Cache_ListsEveryIndexItHoldsWithItsSolutionAndItsSize()
    {
        using var first = FixtureSolution.CreateLibrary();
        using var second = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", first.SolutionPath)).ExitCode);
        Assert.Equal(0, (await InvokeAsync("index", "--solution", second.SolutionPath)).ExitCode);

        var result = await InvokeAsync("cache");

        Assert.Equal(0, result.ExitCode);
        // Through RealPath, which is the spelling the index records and the only one two
        // verbs can be sure they agree on: on macOS the temp directory is reached through
        // /var, which is a link to /private/var.
        Assert.Contains(RealPath.Of(first.SolutionPath), result.Output);
        Assert.Contains(RealPath.Of(second.SolutionPath), result.Output);
        Assert.Contains("2 index(es)", result.Output);
    }

    [Fact]
    public async Task Cache_Clear_RefusesToGuessWhichIndexWasMeant()
    {
        using var cache = new TempCacheHome();

        var result = await InvokeAsync("cache", "clear");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--all", result.Output);
    }

    [Fact]
    public async Task CacheClearSolution_RemovesThatIndexAndLeavesTheOther()
    {
        using var kept = FixtureSolution.CreateLibrary();
        using var removed = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", kept.SolutionPath)).ExitCode);
        Assert.Equal(0, (await InvokeAsync("index", "--solution", removed.SolutionPath)).ExitCode);

        var result = await InvokeAsync("cache", "clear", "--solution", removed.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(IndexPaths.ForSolution(removed.SolutionPath)));
        Assert.True(File.Exists(IndexPaths.ForSolution(kept.SolutionPath)));
    }

    [Fact]
    public async Task CacheClearAll_RemovesEveryIndex()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var result = await InvokeAsync("cache", "clear", "--all");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(IndexPaths.ForSolution(fx.SolutionPath)));
        Assert.Contains("1 index(es)", result.Output);
    }

    [Fact]
    public async Task CacheClearOrphaned_RemovesOnlyTheIndexWhoseSolutionHasGone()
    {
        using var kept = FixtureSolution.CreateLibrary();
        using var doomed = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", kept.SolutionPath)).ExitCode);

        // Indexed, then the solution file deleted where it stood. That is the case vela can
        // be SURE about, and the only one it acts on: the directory the solution lived in is
        // right there and readable, and the .sln is not in it. No further `vela index` runs
        // here, deliberately: one would sweep the orphan itself, and this test is about the
        // verb rather than about the sweep.
        Assert.Equal(0, (await InvokeAsync("index", "--solution", doomed.SolutionPath)).ExitCode);
        var orphanedIndexPath = IndexPaths.ForSolution(doomed.SolutionPath);
        File.Delete(doomed.SolutionPath);

        Assert.True(File.Exists(orphanedIndexPath));

        var result = await InvokeAsync("cache", "clear", "--orphaned");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(orphanedIndexPath));
        Assert.True(File.Exists(IndexPaths.ForSolution(kept.SolutionPath)));
    }

    [Fact]
    public async Task Index_RemovesAnOrphanedIndexAndSaysWhichAndWhy()
    {
        // The one thing that runs without being asked for, and the one that cannot
        // surprise anybody: the solution it describes is not on disk any more, so it is
        // not an index somebody was about to use. "Not on disk" means the directory that
        // held it answered and the file is not in it - see the two tests below for what
        // vela refuses to conclude from a directory that did not answer at all.
        using var kept = FixtureSolution.CreateLibrary();
        using var doomed = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", doomed.SolutionPath)).ExitCode);
        var orphaned = IndexPaths.ForSolution(doomed.SolutionPath);
        File.Delete(doomed.SolutionPath);

        var result = await InvokeAsync("index", "--solution", kept.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(orphaned));
        Assert.Contains("is not there", result.Output);
        Assert.True(File.Exists(IndexPaths.ForSolution(kept.SolutionPath)));
    }

    [Fact]
    public async Task Index_KeepsAnIndexWhoseSolutionIsUnreachableRatherThanGone()
    {
        // The external drive is unplugged, or the NFS mount is stale, or the container is
        // missing a bind mount, or the volume has not been unlocked yet. File.Exists on the
        // .sln says exactly what it says for a file somebody deleted, and acting on that
        // destroys an index whose solution is fine and will be back in a moment.
        using var kept = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        var unplugged = FixtureSolution.CreateLibrary();
        var elsewhere = unplugged.Root + "-unplugged";

        try
        {
            Assert.Equal(0, (await InvokeAsync("index", "--solution", unplugged.SolutionPath)).ExitCode);
            var index = IndexPaths.ForSolution(unplugged.SolutionPath);

            Directory.Move(unplugged.Root, elsewhere);
            Assert.False(Directory.Exists(unplugged.Root), "the fixture is out of reach, not deleted");

            var result = await InvokeAsync("index", "--solution", kept.SolutionPath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(index), "an unreachable solution is not evidence of a deleted one");
            Assert.DoesNotContain("is not there", result.Output);

            // Plugged back in, and the index is still the one that was built for it.
            Directory.Move(elsewhere, unplugged.Root);
            Assert.True(File.Exists(index));

            var listed = await InvokeAsync("cache");
            Assert.Contains(RealPath.Of(unplugged.SolutionPath), listed.Output);
        }
        finally
        {
            unplugged.Dispose();
            try { Directory.Delete(elsewhere, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [UnixOnlyFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Index_KeepsAnIndexWhoseSolutionDirectoryCannotBeRead()
    {
        // The other half of the same fact. A directory the caller cannot traverse answers
        // "no such file" for everything inside it, and vela knows nothing about what is in
        // there - which is not the same as knowing there is nothing in there.
        using var kept = FixtureSolution.CreateLibrary();
        using var unreadable = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", unreadable.SolutionPath)).ExitCode);
        var index = IndexPaths.ForSolution(unreadable.SolutionPath);

        var mode = File.GetUnixFileMode(unreadable.Root);
        File.SetUnixFileMode(unreadable.Root, UnixFileMode.None);

        try
        {
            Assert.False(File.Exists(unreadable.SolutionPath), "the file is there; the caller cannot see it");

            var result = await InvokeAsync("index", "--solution", kept.SolutionPath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(index), "a directory vela cannot read is not evidence of anything");
            Assert.DoesNotContain("is not there", result.Output);
        }
        finally
        {
            File.SetUnixFileMode(unreadable.Root, mode);
        }
    }

    [Fact]
    public async Task Index_RemovesNoOrphanWhenEvictionIsTurnedOff()
    {
        // VELA_CACHE_MAX_BYTES=0 turns off automatic eviction, not one rule of it. Somebody
        // who has switched it off does not expect vela to delete an index on their behalf,
        // and `vela cache clear --orphaned` is still there for the day they want it.
        using var kept = FixtureSolution.CreateLibrary();
        using var doomed = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget("0");

        Assert.Equal(0, (await InvokeAsync("index", "--solution", doomed.SolutionPath)).ExitCode);
        var orphaned = IndexPaths.ForSolution(doomed.SolutionPath);
        File.Delete(doomed.SolutionPath);

        var result = await InvokeAsync("index", "--solution", kept.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(orphaned), "eviction was turned off");
        Assert.DoesNotContain("is not there", result.Output);
    }

    [Fact]
    public async Task Index_NeverRemovesTheIndexItHasJustWritten()
    {
        // A budget of one byte is over-budget by construction, so if anything protects
        // this index it is the rule and not the arithmetic.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget("1");

        var result = await InvokeAsync("index", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(IndexPaths.ForSolution(fx.SolutionPath)));
    }

    [Fact]
    public async Task Index_KeepsAnIndexBuiltThisWeekEvenWhenTheCacheIsOverBudget()
    {
        // The conservative half of the size rule. Somebody who indexed a second solution
        // this morning is exactly the person about to query it, and taking it away to save
        // disk would be the surprise this policy exists to avoid.
        using var first = FixtureSolution.CreateLibrary();
        using var second = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget("1");

        Assert.Equal(0, (await InvokeAsync("index", "--solution", first.SolutionPath)).ExitCode);
        Assert.Equal(0, (await InvokeAsync("index", "--solution", second.SolutionPath)).ExitCode);

        Assert.True(File.Exists(IndexPaths.ForSolution(first.SolutionPath)));
        Assert.True(File.Exists(IndexPaths.ForSolution(second.SolutionPath)));
    }

    [Fact]
    public async Task Index_RemovesTheLeastRecentlyBuiltIndexOnceItIsOldAndTheCacheIsOverBudget()
    {
        using var stale = FixtureSolution.CreateLibrary();
        using var current = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget("1");

        Assert.Equal(0, (await InvokeAsync("index", "--solution", stale.SolutionPath)).ExitCode);
        var stalePath = IndexPaths.ForSolution(stale.SolutionPath);

        // Nothing in vela can make a month pass, so the clock is moved instead. The rule
        // reads the file's own modification time, which is when that index was last built.
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-40));

        var result = await InvokeAsync("index", "--solution", current.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(stalePath));
        Assert.Contains("least recently built", result.Output);
        Assert.True(File.Exists(IndexPaths.ForSolution(current.SolutionPath)));
    }

    [Fact]
    public async Task Index_RemovesNothingWhenTheCacheIsUnderBudget()
    {
        // The default budget is generous, and the ordinary run has to say nothing at all
        // about the cache. A verb that reports housekeeping on every invocation is a verb
        // whose output stops being read.
        using var stale = FixtureSolution.CreateLibrary();
        using var current = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget(null);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", stale.SolutionPath)).ExitCode);
        var stalePath = IndexPaths.ForSolution(stale.SolutionPath);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-400));

        var result = await InvokeAsync("index", "--solution", current.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(stalePath));
        Assert.DoesNotContain("least recently built", result.Output);
    }

    [Fact]
    public async Task Index_EvictsNothingAtAllWhenTheBudgetIsTurnedOff()
    {
        using var stale = FixtureSolution.CreateLibrary();
        using var current = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget("0");

        Assert.Equal(0, (await InvokeAsync("index", "--solution", stale.SolutionPath)).ExitCode);
        var stalePath = IndexPaths.ForSolution(stale.SolutionPath);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-400));

        Assert.Equal(0, (await InvokeAsync("index", "--solution", current.SolutionPath)).ExitCode);

        Assert.True(File.Exists(stalePath));
    }

    [Fact]
    public async Task Query_NeverRemovesAnything()
    {
        // A query is read-only and stays read-only. Anything else would mean the moment a
        // user is most likely to be about to use another index is the moment vela is most
        // likely to take it away.
        using var stale = FixtureSolution.CreateLibrary();
        using var current = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget("1");

        Assert.Equal(0, (await InvokeAsync("index", "--solution", stale.SolutionPath)).ExitCode);
        var stalePath = IndexPaths.ForSolution(stale.SolutionPath);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-400));

        Assert.Equal(0, (await InvokeAsync("index", "--solution", current.SolutionPath)).ExitCode);
        Assert.False(File.Exists(stalePath), "the indexing run should already have evicted it");

        Assert.Equal(0, (await InvokeAsync("index", "--solution", stale.SolutionPath)).ExitCode);
        var rebuilt = IndexPaths.ForSolution(stale.SolutionPath);
        File.SetLastWriteTimeUtc(rebuilt, DateTime.UtcNow.AddDays(-400));

        var query = await InvokeAsync("refs", "Solo.Thing.Value()", "--solution", current.SolutionPath);

        Assert.Equal(0, query.ExitCode);
        Assert.True(File.Exists(rebuilt), "a query must never evict an index");
    }

    [Fact]
    public async Task Cache_NeverListsOrRemovesTheFileARebuildIsBuildingInto()
    {
        // The build file sits beside the index and is owned by the run that is writing it.
        // Listing it would show a reader a file that is not an index, and evicting it would
        // destroy a rebuild in progress from another process.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        using var budget = new CacheBudget("1");

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var building = IndexPaths.TemporaryFor(IndexPaths.ForSolution(fx.SolutionPath));
        File.WriteAllText(building, "half a database, being written by somebody else");
        File.SetLastWriteTimeUtc(building, DateTime.UtcNow.AddDays(-400));

        var listed = await InvokeAsync("cache");
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains("1 index(es)", listed.Output);

        var cleared = await InvokeAsync("cache", "clear", "--all");
        Assert.Equal(0, cleared.ExitCode);
        Assert.True(File.Exists(building), "the build file is not the cache's to remove");
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

    /// <summary>Points VELA_CACHE_MAX_BYTES somewhere useful, and puts it back.</summary>
    private sealed class CacheBudget : IDisposable
    {
        private readonly string? _previous;

        public CacheBudget(string? value)
        {
            _previous = Environment.GetEnvironmentVariable("VELA_CACHE_MAX_BYTES");
            Environment.SetEnvironmentVariable("VELA_CACHE_MAX_BYTES", value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("VELA_CACHE_MAX_BYTES", _previous);
    }

    /// <summary>Points XDG_CACHE_HOME at a disposable directory, and puts it back.</summary>
    private sealed class TempCacheHome : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TempCacheHome()
        {
            _path = Path.Combine(Path.GetTempPath(), "vela-cache-" + Guid.NewGuid().ToString("N")[..8]);
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
