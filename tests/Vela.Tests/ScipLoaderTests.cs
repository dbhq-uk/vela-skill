using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

// Two tests in this class point XDG_CACHE_HOME at a fixture directory, and Task 8's
// CLI tests do the same. An environment variable is process-wide, so those classes
// share a collection with parallelisation disabled and can never overlap.
[Collection(EnvironmentSensitive.Name)]
public class ScipLoaderTests
{
    [Fact]
    public async Task Load_PopulatesDocumentsAndOccurrences()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var index = (await ScipEmitter.EmitAsync(load.Solution, load.Failures, default)).Index;

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        Assert.Equal(index.Documents.Count, ScalarInt(db, "SELECT COUNT(*) FROM document"));
        Assert.True(ScalarInt(db, "SELECT COUNT(*) FROM occurrence") > 0);
    }

    [Fact]
    public void Schema_CreatesAnFts5SymbolIndex()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ('Perfume.Status')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_fts WHERE symbol_fts MATCH 'Status'";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void IndexPaths_ResolvesUnderTheCacheDirectoryAndDependsOnTheSolutionPath()
    {
        // Constraint 2/3: indexing must not write into the repository. The previous
        // version of this test only asserted the resolved path did not start with a
        // fresh /tmp/vela-fx-* fixture root, which is true of almost any path
        // (including a hardcoded constant that ignores solutionPath entirely) and
        // never actually exercised XDG_CACHE_HOME. This version pins the cache root
        // to a known location and checks the path is genuinely derived from
        // solutionPath: two different solutions resolve to two different paths, and
        // the same solution resolves to the same path twice (it is a content hash).
        //
        // Fixtures are created before the environment mutation so only the direct,
        // in-process calls to IndexPaths.ForSolution - a pure function with no I/O -
        // run inside the try block. That keeps the window in which XDG_CACHE_HOME is
        // overridden as small as possible. The other tests in this assembly that
        // read or set XDG_CACHE_HOME (Task 8's CLI tests) sit in the same
        // non-parallel collection as this class, so none of them can run while this
        // override is in place, and the previous value is restored in a finally
        // block so a failure here cannot leak state into any test that runs
        // afterwards.
        using var fxA = FixtureSolution.CreateEmptySolution();
        using var fxB = FixtureSolution.CreateEmptySolution();

        var cacheRoot = Path.Combine(Path.GetTempPath(), "vela-cache-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string pathA1, pathA2, pathB;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cacheRoot);
            pathA1 = IndexPaths.ForSolution(fxA.SolutionPath);
            pathA2 = IndexPaths.ForSolution(fxA.SolutionPath);
            pathB = IndexPaths.ForSolution(fxB.SolutionPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
        }

        var fullCacheRoot = Path.GetFullPath(cacheRoot);
        Assert.StartsWith(fullCacheRoot + Path.DirectorySeparatorChar, pathA1, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pathA1, pathA2);
        Assert.NotEqual(pathA1, pathB);
    }

    [Fact]
    public void ForSolution_GuardsAgainstTheCacheDirectoryLandingInsideTheSolution()
    {
        // Finding 2: if the resolved cache directory is inside the repository being
        // indexed, vela must refuse loudly rather than silently write the index into
        // the repository it is indexing (Constraint 2). This must not be fooled by a
        // sibling directory that merely shares a string prefix with the solution
        // root (e.g. "/repo-backup" against "/repo").
        using var fx = FixtureSolution.CreateEmptySolution();
        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            // Exactly the solution directory, with a trailing separator: must be
            // caught, not just a strict subdirectory.
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", fx.Root + Path.DirectorySeparatorChar);
            var exactMatch = Assert.Throws<InvalidOperationException>(() => IndexPaths.ForSolution(fx.SolutionPath));
            Assert.Contains("XDG_CACHE_HOME", exactMatch.Message);

            // A subdirectory nested under the solution directory: must also be caught.
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(fx.Root, "nested", "cache"));
            var nested = Assert.Throws<InvalidOperationException>(() => IndexPaths.ForSolution(fx.SolutionPath));
            Assert.Contains("XDG_CACHE_HOME", nested.Message);

            // A sibling directory that only shares a string prefix must NOT be caught.
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", fx.Root.TrimEnd(Path.DirectorySeparatorChar) + "-backup");
            var siblingPath = IndexPaths.ForSolution(fx.SolutionPath);
            Assert.False(string.IsNullOrEmpty(siblingPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
        }
    }

    [Fact]
    public void Load_ThrowsWhenTheDatabaseAlreadyContainsData()
    {
        // Finding 3: ScipLoader.Load requires an empty schema. Schema.Create uses
        // CREATE TABLE IF NOT EXISTS and never truncates, so re-running Load against
        // a database that already holds an index - a normal re-index - must fail
        // with an intelligible message rather than a raw SqliteException from the
        // relative_path UNIQUE constraint.
        var index = MinimalIndex();

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        var ex = Assert.Throws<InvalidOperationException>(() => ScipLoader.Load(db, index));
        Assert.Contains("delete the file and re-index", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalDocumentPaths_RecoverWhichFilesWereSkipped_NotJustHowMany()
    {
        // A count with no way to see what it counted is not diagnosable. The paths
        // existed only in the emitted index's tool arguments, which are never persisted
        // and never written out as SCIP, so once `vela index` had printed "1
        // document(s) ... were not indexed" nothing could say which one. If the
        // classification is ever wrong about a file, that is the difference between a
        // five-second check and a hole in the index nobody can explain.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ToolInfo = new Scip.ToolInfo { Name = "vela", Version = "0.0.0" } }
        };

        index.Metadata.ToolInfo.Arguments.Add("external-document: /cache/microsoft.net.test.sdk/Program.cs");
        index.Metadata.ToolInfo.Arguments.Add("external-document: /dotnet/shared/Framework.cs");

        // The degrading channel is a different question with a different answer, and it
        // already reaches the reader through the banner.
        index.Metadata.ToolInfo.Arguments.Add("outside-project-root: /elsewhere/Shared/Foo.cs");
        index.Metadata.ToolInfo.Arguments.Add("load-failure: App.csproj did not load");

        var paths = Program.ExternalDocumentPaths(index);

        Assert.Equal(
            new[] { "/cache/microsoft.net.test.sdk/Program.cs", "/dotnet/shared/Framework.cs" },
            paths);

        // The number `vela index` prints is derived from this same list, so the count
        // and the list can never disagree.
        Assert.Equal(paths.Count, Program.CountExternalDocuments(index));
    }

    [Fact]
    public void ExternalDocuments_ArePersistedWithTheIndexAndNamedByStats()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        // Nothing was skipped, so nothing is said. A stock solution's --stats output is
        // exactly what it was, which is what AGENTS.md documents as the coverage check.
        var quiet = IndexStatistics.Render(IndexStatistics.Read(db));
        Assert.DoesNotContain("external", quiet, StringComparison.OrdinalIgnoreCase);

        var skipped = new[]
        {
            "/home/dev/.nuget/packages/microsoft.net.test.sdk/18.4.0/build/net8.0/Microsoft.NET.Test.Sdk.Program.cs",
            "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/Generated.cs"
        };

        ExternalDocuments.Write(db, skipped);

        // Persisted, so the answer outlives the process that worked it out.
        var stats = IndexStatistics.Read(db);
        Assert.Equal(skipped, stats.ExternalDocuments);

        var rendered = IndexStatistics.Render(stats);
        Assert.Contains("external documents", rendered, StringComparison.Ordinal);
        foreach (var path in skipped)
            Assert.Contains(path, rendered, StringComparison.Ordinal);
    }

    private static Scip.Index MinimalIndex()
    {
        var index = new Scip.Index();
        var doc = new Scip.Document { RelativePath = "Foo.cs", Language = "csharp" };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = "scip-csharp cargo Foo 1.0.0 Foo#",
            SymbolRoles = (int)Scip.SymbolRole.Definition
        });
        index.Documents.Add(doc);
        return index;
    }

    private static int ScalarInt(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
