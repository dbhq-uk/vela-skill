using System.CommandLine;
using Microsoft.Data.Sqlite;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// A failed rebuild must leave the index that was there exactly as it was.
///
/// `vela index` used to delete the database and then write a new one. Interrupt it -
/// Ctrl-C, an OOM kill, a full disk, a project that throws halfway through the harvest -
/// and there was no index at all, rather than the one there had been. On a solution where
/// a full index takes minutes that is a bad trade for a moment's inattention, and the user
/// who pays it is the one who was not watching.
///
/// These tests drive the real command against a real solution and inject a real write
/// failure, because the property is about what is on disk after something went wrong and
/// nothing short of the filesystem can say that. Indexing through the CLI resolves the
/// index path from XDG_CACHE_HOME, which is process-wide, so this class shares the
/// non-parallel collection with every other test that touches it.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class AtomicIndexTests
{
    [Fact]
    public async Task Index_LeavesThePreviousIndexUntouchedWhenTheRebuildCannotBeWritten()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        var first = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(0, first.ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        var before = File.ReadAllBytes(indexPath);

        // A rebuild that cannot write. A directory standing where the build file goes is
        // this suite's stand-in for a full disk or a kill signal: the run fails after it
        // has committed to rebuilding and before it has anything to put in place, which is
        // the window that used to destroy the index.
        var building = IndexPaths.TemporaryFor(indexPath);
        Directory.CreateDirectory(building);

        var failed = await InvokeAsync("index", "--solution", fx.SolutionPath);

        // The failure is REPORTED, not thrown. This assertion used to be
        // Assert.ThrowsAnyAsync, which encoded the defect: the index survived and the user
        // saw a .NET stack trace instead of being told what had happened. Exit 1 is the
        // existing "vela could not do what was asked" code, and every property the throwing
        // version proved is still proved below.
        Assert.Equal(Program.ExitCannotAnswer, failed.ExitCode);
        Assert.Contains("could not write the index", failed.Output);
        Assert.Contains(indexPath, failed.Output);
        Assert.Contains("is exactly as it was", failed.Output);
        Assert.Contains("run vela index again", failed.Output);
        Assert.DoesNotContain("Unhandled exception", failed.Output);
        Assert.DoesNotContain("   at ", failed.Output);

        // Byte-identical. Not "an index exists", not "a query answers": the old one, the
        // one the user had, unchanged.
        Assert.Equal(before, File.ReadAllBytes(indexPath));

        // And it is still an index rather than a file of the right length. It answers, and
        // it still vouches for itself, which is what the exit code of every verb hangs on.
        using var db = Open(indexPath);
        var health = IndexHealth.Read(db);
        Assert.False(health.Degraded, health.Detail);
        Assert.NotEmpty(RefsQuery.Run(db, "Solo.Thing.Value()"));

        var query = await InvokeAsync("refs", "Solo.Thing.Value()", "--solution", fx.SolutionPath);
        Assert.Equal(0, query.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", query.Output);
    }

    [Fact]
    public async Task Index_RemovesTheBuildFileWhenTheRebuildFailsAtTheLastMoment()
    {
        // The other half of the promise: the build file is not left behind. A cache
        // directory filling up with half-built databases nobody can name is its own defect,
        // and this one fails at the very end - the new index is complete and the move into
        // place is what cannot happen - so the temporary file definitely exists by then.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        IndexPaths.EnsureDirectoryExists(indexPath);
        Directory.CreateDirectory(indexPath);

        var failed = await InvokeAsync("index", "--solution", fx.SolutionPath);

        // Reported rather than thrown, for the same reason as above, and in the words this
        // failure alone earns: the index is finished and healthy and only the last rename
        // would not happen, which on Windows is what another process holding the
        // destination open looks like.
        Assert.Equal(Program.ExitCannotAnswer, failed.ExitCode);
        Assert.Contains("could not move it into place", failed.Output);
        Assert.Contains("another process holding the index open", failed.Output);
        Assert.DoesNotContain("Unhandled exception", failed.Output);
        Assert.DoesNotContain("   at ", failed.Output);

        Assert.False(File.Exists(IndexPaths.TemporaryFor(indexPath)),
            "the build file was left in the cache directory after a failed rebuild");
    }

    /// <summary>
    /// The Windows failure this whole message exists for, made on Windows.
    ///
    /// A rename over a file another process has open fails there and succeeds on Unix, so
    /// this is the one platform where the real cause can be produced rather than stood in
    /// for. The test above reaches the same code path everywhere by putting a directory in
    /// the way; this one holds a handle, which is what an editor, an agent or a second vela
    /// actually does.
    /// </summary>
    [WindowsOnlyFact]
    public async Task Index_ReportsThatSomethingElseHasTheIndexOpen()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        var before = File.ReadAllBytes(indexPath);

        int exitCode;
        string output;

        // FileShare.Read denies the delete sharing a rename over this file needs, which is
        // exactly what any other reader of the index holds.
        using (var holder = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            (exitCode, output) = await InvokeAsync("index", "--solution", fx.SolutionPath);
        }

        Assert.Equal(Program.ExitCannotAnswer, exitCode);
        Assert.Contains("could not move it into place", output);
        Assert.Contains("another process holding the index open", output);
        Assert.DoesNotContain("Unhandled exception", output);

        Assert.Equal(before, File.ReadAllBytes(indexPath));
        Assert.False(File.Exists(IndexPaths.TemporaryFor(indexPath)));

        var query = await InvokeAsync("refs", "Solo.Thing.Value()", "--solution", fx.SolutionPath);
        Assert.Equal(0, query.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", query.Output);
    }

    [Fact]
    public async Task Index_LeavesNoBuildFileBehindWhenItSucceeds()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        var result = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(0, result.ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        Assert.True(File.Exists(indexPath));
        Assert.False(File.Exists(IndexPaths.TemporaryFor(indexPath)));
    }

    [Fact]
    public async Task Index_ClearsADeadBuildFileLeftBehindByAKilledRun()
    {
        // A process killed outright cannot clean up after itself, so the file it was
        // building into is still there on the next run. It is not evidence of anything and
        // it must not stop the rebuild: the run that finds it owns it.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        IndexPaths.EnsureDirectoryExists(indexPath);
        File.WriteAllText(IndexPaths.TemporaryFor(indexPath), "not a database, and half written");

        var result = await InvokeAsync("index", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(IndexPaths.TemporaryFor(indexPath)));

        using var db = Open(indexPath);
        Assert.NotEmpty(RefsQuery.Run(db, "Solo.Thing.Value()"));
    }

    [Fact]
    public async Task IncrementalIndex_LeavesThePreviousIndexUntouchedWhenTheRebuildCannotBeWritten()
    {
        // --incremental is the mode where this is hardest to get right, because it reads
        // the PREVIOUS index to decide what to rebuild and to keep the rows it is not
        // rebuilding. It works on a copy and moves it into place, so the index it read is
        // still the index on disk if anything goes wrong.
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        var first = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(0, first.ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        var before = File.ReadAllBytes(indexPath);

        fx.Write("Lib/Upstream.cs", """
            namespace Lib
            {
                public static class Upstream
                {
                    public static long Twice(long value) => value + value;
                }
            }
            """);

        var building = IndexPaths.TemporaryFor(indexPath);
        Directory.CreateDirectory(building);

        var failed = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);

        Assert.Equal(Program.ExitCannotAnswer, failed.ExitCode);
        Assert.Contains("could not write the index", failed.Output);
        Assert.DoesNotContain("Unhandled exception", failed.Output);
        Assert.DoesNotContain("   at ", failed.Output);

        Assert.Equal(before, File.ReadAllBytes(indexPath));

        using var db = Open(indexPath);
        Assert.False(IndexHealth.Read(db).Degraded);
        Assert.Contains("Lib.Upstream.Twice(System.Int32)", SymbolsIn(db, "App/Caller.cs"));
    }

    [Fact]
    public async Task IncrementalIndex_StillReusesProjectsWhenItBuildsThroughATemporaryFile()
    {
        // The build file must not cost --incremental the thing it exists for. The plan is
        // taken from the index that is on disk and the rows it reuses are carried into the
        // copy, so a run that rebuilds two of three projects still rebuilds two of three.
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        fx.Write("Lib/Upstream.cs", """
            namespace Lib
            {
                public static class Upstream
                {
                    public static long Twice(long value) => value + value;
                }
            }
            """);

        var second = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);

        Assert.Equal(0, second.ExitCode);
        Assert.Contains("2 of 3 project(s) rebuilt", second.Output);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        Assert.False(File.Exists(IndexPaths.TemporaryFor(indexPath)));

        using var db = Open(indexPath);
        Assert.Contains("Lib.Upstream.Twice(System.Int64)", SymbolsIn(db, "App/Caller.cs"));
    }

    private static IReadOnlyList<string> SymbolsIn(SqliteConnection db, string relativePath)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT o.symbol FROM occurrence o JOIN document d ON d.id = o.document_id "
            + "WHERE d.relative_path = $p";
        cmd.Parameters.AddWithValue("$p", relativePath);
        using var reader = cmd.ExecuteReader();

        var symbols = new List<string>();
        while (reader.Read()) symbols.Add(reader.GetString(0));
        return symbols;
    }

    private static SqliteConnection Open(string indexPath)
    {
        var connectionString =
            new SqliteConnectionStringBuilder { DataSource = indexPath, Pooling = false }.ToString();
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
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
            _path = Path.Combine(Path.GetTempPath(), "vela-atomic-" + Guid.NewGuid().ToString("N")[..8]);
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
