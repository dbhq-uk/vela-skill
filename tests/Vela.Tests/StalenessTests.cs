using Vela.Indexing;
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
