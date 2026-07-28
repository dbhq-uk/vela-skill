using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

public class ScipLoaderTests
{
    [Fact]
    public async Task Load_PopulatesDocumentsAndOccurrences()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

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
    public void IndexPaths_ResolvesOutsideTheSolutionDirectory()
    {
        // Constraint 3: indexing must not write into the repository.
        using var fx = FixtureSolution.CreateWebApp();
        var path = IndexPaths.ForSolution(fx.SolutionPath);
        Assert.False(path.StartsWith(fx.Root, StringComparison.OrdinalIgnoreCase));
    }

    private static int ScalarInt(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
