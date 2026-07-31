using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// What the cache directory holds, and the only circumstances in which vela removes any of
/// it.
///
/// <b>The problem.</b> Nothing had ever removed an index. The development machine's
/// `~/.cache/vela` stood at 983MB across five databases and could only grow: index a few
/// solutions, or one solution under two spellings, and a user who never thinks about the
/// cache eventually notices their disk. An index is a cache and a rebuild is minutes, so
/// leaning towards eviction is defensible - but an index taken away from somebody who was
/// about to use it costs them exactly those minutes, without warning, at the moment they
/// least wanted to spend them.
///
/// <b>The policy, and why it is this one.</b>
///
///   1. `vela cache` lists what is held, with the solution each index is of, its size and
///      when it was last built. Nothing else in vela could answer "what is this 983MB",
///      and a policy nobody can see the input to is a policy nobody can trust.
///   2. `vela cache clear` removes what the user names: one solution's index,
///      every orphan, or everything.
///   3. `vela index` removes ORPHANS - an index whose solution file is no longer on disk.
///      This is the one automatic rule that cannot surprise anybody, because the thing it
///      describes is gone.
///   4. `vela index` removes the LEAST RECENTLY BUILT indexes, and only while the whole
///      cache is over a size budget. Never the index the run just wrote, and never one
///      built within the last week. Both exclusions exist to stop the case that would be a
///      surprise: somebody working across two solutions this morning.
///
/// Everything automatic happens during `vela index` and nowhere else. A query is read-only
/// and stays read-only; and the moment a user runs one is the moment they are most likely
/// to be about to use another index, which is the worst possible moment to take one away.
///
/// <b>What was rejected.</b> Eviction by last ACCESS time, which is the obvious reading of
/// "least recently used": `relatime` and `noatime` are the norm on Linux and macOS, so an
/// index queried every day can carry a months-old access time and would be evicted for
/// being popular. Recording a last-used timestamp in the index instead, which turns every
/// query into a write, contends with concurrent readers and gives up the read-only
/// property outright. A limit on the NUMBER of indexes, which says nothing about disk:
/// five large ones are 1.4GB and fifty small ones are 200MB. And deleting an index whose
/// schema this build cannot read, which is tempting because such an index is useless to
/// the build in front of you - and wrong, because a user who downgrades vela would find
/// the index the older build could have read already destroyed.
///
/// <b>An index being read is never destroyed under its reader.</b> On Linux and macOS,
/// unlinking a file another process has open leaves that process reading it to the end;
/// the bytes go when the last handle closes. On Windows the delete fails outright, and
/// that failure is caught, counted and reported rather than being allowed to take the
/// command down: the index stays, which is the safe direction.
/// </summary>
public static class IndexCache
{
    /// <summary>
    /// How much the whole cache directory may hold before `vela index` starts removing the
    /// least recently built indexes: 2 GiB.
    ///
    /// Chosen to be over the size real use had already reached rather than under it. The
    /// machine this was written on sat at 983MB, and a default that evicted on the day it
    /// shipped would have been a change of behaviour disguised as a bug fix. What this
    /// stops is unbounded growth, which is the actual defect.
    /// </summary>
    public const long DefaultMaximumBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Overrides <see cref="DefaultMaximumBytes"/>. 0 turns size-based eviction off
    /// entirely, leaving only the orphan rule, which removes nothing anybody could want.
    /// </summary>
    public const string MaximumBytesVariable = "VELA_CACHE_MAX_BYTES";

    /// <summary>
    /// How recently an index must have been built to be safe from the size rule. A week
    /// covers "I am working across these two repositories at the moment", which is the only
    /// case where losing an index is a real cost rather than a rebuild nobody notices.
    /// </summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Every index in the cache directory, ordered by path so the same cache renders the
    /// same list on every run and every machine (Constraint 1).
    ///
    /// The solution each index is of is read from the index itself. An index that will not
    /// open, or that predates the record, comes back with no solution named - it is still
    /// listed, with its size, because a file taking up disk is worth seeing whether or not
    /// anything can say what it was for. It is never treated as an orphan on that basis:
    /// "vela cannot tell what this is of" and "the thing this is of has gone" are different
    /// facts, and deleting on the first would delete on ignorance.
    /// </summary>
    public static IReadOnlyList<CachedIndex> List(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory)) return Array.Empty<CachedIndex>();

        var found = new List<CachedIndex>();

        foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.db")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            long bytes;
            DateTime builtAtUtc;

            try
            {
                var info = new FileInfo(path);
                bytes = info.Length;
                builtAtUtc = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            found.Add(new CachedIndex(path, SolutionOf(path), bytes, builtAtUtc));
        }

        return found;
    }

    /// <summary>
    /// The solution an index is of, or null when nothing in it can say.
    ///
    /// Read-only, and tolerant of everything. This opens files nobody asked about - the
    /// point is to describe the whole directory - so an index from a future build of vela,
    /// a half-written file, or something that is not a database at all must all be
    /// survivable. Every one of them comes back as "no solution named".
    /// </summary>
    private static string? SolutionOf(string indexPath)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = indexPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var db = new SqliteConnection(connectionString);
            db.Open();
            return IndexIdentity.Read(db);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The housekeeping `vela index` does once its own index is safely in place: orphans
    /// first, then the least recently built while the cache is over budget.
    /// </summary>
    /// <param name="keepPath">
    /// The index this run just wrote, which is never removed whatever the arithmetic says.
    /// </param>
    /// <param name="maximumBytes">
    /// The budget. 0 or less turns the size rule off and leaves only the orphan rule.
    /// </param>
    /// <param name="nowUtc">
    /// Passed in rather than read, so the age rule can be tested without waiting a week.
    /// </param>
    public static EvictionReport Sweep(
        string cacheDirectory, string keepPath, long maximumBytes, DateTime nowUtc)
    {
        var held = List(cacheDirectory);
        var removed = new List<EvictedIndex>();
        var refused = 0;

        var survivors = new List<CachedIndex>();

        foreach (var index in held)
        {
            if (IsSame(index.Path, keepPath) || !index.IsOrphaned)
            {
                survivors.Add(index);
                continue;
            }

            if (Remove(index.Path))
                removed.Add(new EvictedIndex(index, $"the solution it was built from, {index.SolutionPath}, is not there"));
            else
            {
                refused++;
                survivors.Add(index);
            }
        }

        if (maximumBytes <= 0) return new EvictionReport(removed, refused, TotalBytes(survivors), maximumBytes);

        // Oldest first, and the tie broken on the path, so the same cache evicts the same
        // index on every run and every machine (Constraint 1). Two indexes built in the
        // same second is not a hypothetical: a script that indexes several solutions in a
        // row produces exactly that.
        var candidates = survivors
            .Where(index => !IsSame(index.Path, keepPath) && nowUtc - index.BuiltAtUtc > MinimumAge)
            .OrderBy(index => index.BuiltAtUtc)
            .ThenBy(index => index.Path, StringComparer.Ordinal)
            .ToList();

        var total = TotalBytes(survivors);

        foreach (var index in candidates)
        {
            if (total <= maximumBytes) break;

            if (!Remove(index.Path))
            {
                refused++;
                continue;
            }

            total -= index.Bytes;
            removed.Add(new EvictedIndex(index,
                $"it is the least recently built index in a cache over its {Describe(maximumBytes)} budget, "
                + $"and it was last built on {index.BuiltAtUtc:yyyy-MM-dd}"));
        }

        return new EvictionReport(removed, refused, total, maximumBytes);
    }

    /// <summary>
    /// Removes the named indexes, reporting how many went and how many would not.
    /// This is what `vela cache clear` is; the caller has already decided which.
    /// </summary>
    public static ClearReport Clear(IReadOnlyList<CachedIndex> indexes)
    {
        var removed = new List<CachedIndex>();
        var refused = new List<CachedIndex>();

        foreach (var index in indexes)
        {
            if (Remove(index.Path)) removed.Add(index);
            else refused.Add(index);
        }

        return new ClearReport(removed, refused);
    }

    /// <summary>
    /// The budget for this run: <see cref="MaximumBytesVariable"/> when it is set to a
    /// number, and <see cref="DefaultMaximumBytes"/> otherwise. A value that is not a
    /// number is not silently taken as zero, which would turn a typo into "eviction is
    /// off" - it falls back to the default, and the caller says so.
    /// </summary>
    public static long ConfiguredMaximumBytes(out string? complaint)
    {
        complaint = null;

        var configured = Environment.GetEnvironmentVariable(MaximumBytesVariable);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultMaximumBytes;

        if (long.TryParse(configured.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
            && bytes >= 0)
        {
            return bytes;
        }

        complaint = $"{MaximumBytesVariable} is set to '{configured}', which is not a number of bytes, "
                  + $"so the default of {Describe(DefaultMaximumBytes)} was used. Set it to 0 to turn "
                  + "size-based eviction off.";
        return DefaultMaximumBytes;
    }

    /// <summary>A size a person can read, in the units the number is nearest to.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (double)(1024L * 1024 * 1024):0.##}GB",
        >= 1024L * 1024 => $"{bytes / (double)(1024L * 1024):0.##}MB",
        >= 1024 => $"{bytes / 1024.0:0.##}KB",
        _ => $"{bytes}B"
    };

    private static long TotalBytes(IEnumerable<CachedIndex> indexes) => indexes.Sum(index => index.Bytes);

    /// <summary>
    /// Two cache paths naming one file. Ordinal on Linux, where Foo.db and foo.db really
    /// are two files, and case-insensitive on Windows and macOS, where they are not - the
    /// same rule <see cref="IndexPaths"/> applies to the guard on its own cache directory.
    /// </summary>
    private static bool IsSame(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes one index and the sidecars SQLite may have left beside it. False means it
    /// would not go, which on Windows is what an index another process has open looks like.
    /// That is reported rather than thrown: the index stays, which is the safe direction,
    /// and a housekeeping failure must never take down the command that was asked to build
    /// an index.
    /// </summary>
    private static bool Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            try
            {
                var sidecar = path + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The database itself has gone, so its journal describes nothing. Leaving
                // one behind costs a few kilobytes and confuses nobody.
            }
        }

        return true;
    }
}

/// <summary>
/// One index in the cache directory: where it is, what solution it is of, how big it is and
/// when it was last built.
/// </summary>
/// <param name="SolutionPath">
/// Null when the index does not say - an index built before vela recorded it, or one that
/// will not open. Never confused with a solution that has gone: see
/// <see cref="IsOrphaned"/>.
/// </param>
/// <param name="BuiltAtUtc">
/// The file's own modification time, which is when this index was last written - by an
/// indexing run or by an import. It is used rather than the last ACCESS time because
/// `relatime` and `noatime` are the norm, so an access time says almost nothing, and rather
/// than a timestamp inside the database because reading one would mean opening every index
/// on every sweep.
/// </param>
public sealed record CachedIndex(string Path, string? SolutionPath, long Bytes, DateTime BuiltAtUtc)
{
    /// <summary>
    /// The solution this index is of is not on disk. An index vela cannot identify at all
    /// is NOT an orphan: not knowing what something is for is not the same as knowing it is
    /// for nothing, and only the second is a reason to delete it.
    /// </summary>
    public bool IsOrphaned => SolutionPath is not null && !File.Exists(SolutionPath);
}

/// <summary>One index removed, and the sentence saying why.</summary>
public sealed record EvictedIndex(CachedIndex Index, string Reason);

/// <summary>
/// What one automatic sweep did. <paramref name="Refused"/> counts the indexes that would
/// not delete, which on Windows is what one another process has open looks like.
/// </summary>
public sealed record EvictionReport(
    IReadOnlyList<EvictedIndex> Removed, int Refused, long TotalBytes, long MaximumBytes);

/// <summary>What one `vela cache clear` did.</summary>
public sealed record ClearReport(IReadOnlyList<CachedIndex> Removed, IReadOnlyList<CachedIndex> Refused);
