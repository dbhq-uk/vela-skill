using System.Runtime.Versioning;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

/// <summary>
/// Measured on a real 375,608-line solution, the staleness walk stated every one of
/// 50,906 files under the project root on every invocation, which is what made a
/// 1.0s `def` query and a 3.4s `refs` query out of a 0.12s process floor. These
/// tests pin the fix: the walk only ever examines a file whose extension vela
/// indexes, and it never descends into a default-excluded directory, however many
/// files either of those holds.
///
/// The count is asserted rather than the wall clock time, because a timing
/// assertion would be flaky on a shared machine and Constraint 1 requires the same
/// tree to give the same reading on every run.
/// </summary>
public class StalenessTests
{
    [Fact]
    public void Scan_ReportsTheNewestRelevantChangeAndIgnoresEverythingElse()
    {
        using var temp = new TempDirectory();
        var builtAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var changed = builtAt.AddMinutes(5);

        // Irrelevant by extension: never examined, whatever their timestamp.
        WriteAt(Path.Combine(temp.Path, "image.png"), changed);
        WriteAt(Path.Combine(temp.Path, "notes.txt"), changed);

        // Relevant extension, but under a directory vela never walks: build output and
        // a package directory change on their own schedule and describe nothing the
        // index claims to cover.
        var bin = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(bin);
        WriteAt(Path.Combine(bin, "Generated.cs"), changed);

        var nodeModules = Path.Combine(temp.Path, "node_modules", "pkg");
        Directory.CreateDirectory(nodeModules);
        WriteAt(Path.Combine(nodeModules, "Fake.cs"), changed);

        // The one file that should actually be found.
        var relevant = Path.Combine(temp.Path, "src", "Perfume.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(relevant)!);
        WriteAt(relevant, changed);

        var scan = Staleness.Scan(temp.Path, builtAt);

        Assert.Equal(1, scan.ChangedCount);
        Assert.Equal(Path.GetFullPath(relevant), scan.NewestPath);
    }

    [Fact]
    public void Scan_StillSeesDotPrefixedFilesAndDirectories()
    {
        // The bounded walk replaced Directory.EnumerateFileSystemEntries, which asks
        // for EnumerationOptions.Compatible and therefore AttributesToSkip = 0, with a
        // FileSystemEnumerable whose default options skip Hidden and System. On Linux
        // that is every dot-prefixed name; on Windows anything marked hidden. The skip
        // list names the directories vela deliberately ignores, and a tooling directory
        // that happens to begin with a dot is not on it: dropping those silently would
        // narrow a Constraint 3 signal without saying so anywhere.
        using var temp = new TempDirectory();
        var builtAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var changed = builtAt.AddMinutes(5);

        var generated = Path.Combine(temp.Path, ".generated");
        Directory.CreateDirectory(generated);
        WriteAt(Path.Combine(generated, "Foo.cs"), changed);

        // A dot-prefixed file directly under the root, which is the same attribute in
        // a different place.
        WriteAt(Path.Combine(temp.Path, ".Hidden.cs"), builtAt);

        var scan = Staleness.Scan(temp.Path, builtAt);

        Assert.Equal(2, scan.FilesExamined);
        Assert.Equal(1, scan.ChangedCount);
        Assert.Equal(Path.GetFullPath(Path.Combine(generated, "Foo.cs")), scan.NewestPath);
    }

    [Fact]
    public void Scan_FilesExaminedDoesNotScaleWithIrrelevantFileCount()
    {
        using var small = new TempDirectory();
        using var large = new TempDirectory();

        // "Irrelevant" here covers both reasons a file never reaches a stat call: the
        // wrong extension, and the right extension inside a directory that is never
        // walked at all. Neither should cost the walk anything, no matter how many of
        // them there are.
        PopulateIrrelevantFiles(small.Path, count: 50);
        PopulateIrrelevantFiles(large.Path, count: 2000);

        var builtAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteAt(Path.Combine(small.Path, "Relevant.cs"), builtAt);
        WriteAt(Path.Combine(large.Path, "Relevant.cs"), builtAt);

        var smallScan = Staleness.Scan(small.Path, builtAt);
        var largeScan = Staleness.Scan(large.Path, builtAt);

        // Exactly one file examined in both trees: the 40x difference in irrelevant
        // file count between them changed nothing.
        Assert.Equal(1, smallScan.FilesExamined);
        Assert.Equal(1, largeScan.FilesExamined);
    }

    [Fact]
    public void Check_ReportsDegraded_WhenTheRootTheIndexWasBuiltAgainstIsNotThere()
    {
        // The root is resolved at query time by walking up for `.git`, not read back
        // from the index, so a moved or renamed repository, a worktree that has been
        // removed, or a changed `.git` layout can point the check at a directory that
        // does not exist. The walk then finds nothing to compare, and "nothing newer
        // than the index" printed as a clean exit 0 is the most confident wrong answer
        // vela can give: it is a freshness check that did not run, reported as a
        // freshness check that passed.
        var missing = Path.Combine(Path.GetTempPath(), "vela-absent-" + Guid.NewGuid().ToString("N")[..8]);
        var health = new HealthRecord(DateTime.UtcNow, null, false, null);

        var checked_ = Staleness.Check(health, missing);

        Assert.True(checked_.Degraded);
        Assert.Contains("could not be checked", checked_.Detail!, StringComparison.Ordinal);
        Assert.Contains(missing, checked_.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_KeepsAnExistingDegradation_WhenTheRootIsNotThere()
    {
        // Staleness is an additional reason and never a replacement, and a missing
        // root is no different: the build-time reason has to survive it.
        var missing = Path.Combine(Path.GetTempPath(), "vela-absent-" + Guid.NewGuid().ToString("N")[..8]);
        var health = new HealthRecord(DateTime.UtcNow, null, true, "1 project(s) failed to load");

        var checked_ = Staleness.Check(health, missing);

        Assert.True(checked_.Degraded);
        Assert.Contains("1 project(s) failed to load", checked_.Detail!, StringComparison.Ordinal);
        Assert.Contains("could not be checked", checked_.Detail!, StringComparison.Ordinal);
    }

    private static void PopulateIrrelevantFiles(string root, int count)
    {
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);
        for (var i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(assets, $"image{i}.png"), "");

        var nodeModules = Path.Combine(root, "node_modules", "pkg");
        Directory.CreateDirectory(nodeModules);
        for (var i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(nodeModules, $"Fake{i}.cs"), "");
    }

    private static void WriteAt(string path, DateTime timeUtc)
    {
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, timeUtc);
    }

    [UnixOnlyFact]
    [UnsupportedOSPlatform("windows")]
    public void Check_WhenADirectoryCannotBeRead_DegradesRatherThanReportingFresh()
    {
        // A directory the walk cannot list may hold a file newer than the index, so a
        // clean answer from an incomplete walk is a freshness check that did not run
        // being reported as one that passed. Constraint 3 forbids exactly that.
        using var temp = new TempDirectory();
        var builtAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var locked = Path.Combine(temp.Path, "locked");
        Directory.CreateDirectory(locked);
        WriteAt(Path.Combine(locked, "Hidden.cs"), builtAt.AddMinutes(5));

        // Everything readable is older than the index, so without the unreadable
        // directory this walk would report the tree unchanged.
        WriteAt(Path.Combine(temp.Path, "Readable.cs"), builtAt.AddMinutes(-5));

        var mode = File.GetUnixFileMode(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var scan = Staleness.Scan(temp.Path, builtAt);
            Assert.Equal(0, scan.ChangedCount);
            Assert.Equal(1, scan.UnreadableDirectories);

            var health = Staleness.Check(
                new HealthRecord(builtAt, null, Degraded: false, null), temp.Path);

            Assert.True(health.Degraded);
            Assert.Contains("could only be partly checked", health.Detail);
        }
        finally
        {
            File.SetUnixFileMode(locked, mode);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vela-stale-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
