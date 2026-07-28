using Microsoft.Data.Sqlite;
using Vela.Indexing;
using Xunit;

public class IndexHealthTests
{
    [Fact]
    public void Read_AfterWritingDegradedState_ReportsDegraded()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        var writtenAt = DateTime.UtcNow;
        // Write twice: Write must replace, not accumulate, the health row.
        IndexHealth.Write(db, new HealthRecord(writtenAt.AddMinutes(-5), "earlier", Degraded: false, null));
        IndexHealth.Write(db, new HealthRecord(writtenAt, "abc123", Degraded: true, "App.csproj failed to load"));
        var health = IndexHealth.Read(db);

        Assert.True(health.Degraded);
        Assert.NotNull(health.Detail);
        Assert.Contains("App.csproj", health.Detail);
        Assert.Equal(1, RowCount(db));
        // The "O" round trip must preserve UTC kind, not silently shift to Local/Unspecified.
        Assert.Equal(DateTimeKind.Utc, health.BuiltAtUtc.Kind);
        Assert.Equal(writtenAt, health.BuiltAtUtc);
    }

    private static long RowCount(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM index_health";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Read_OnAHealthyIndex_ReportsNotDegraded()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", Degraded: false, null));

        Assert.False(IndexHealth.Read(db).Degraded);
    }

    [Fact]
    public void ExitDegraded_IsDistinctFromSuccessAndFromUsageError()
    {
        // A degraded answer must be distinguishable by a caller, not just by a human.
        Assert.NotEqual(0, IndexHealth.ExitDegraded);
        Assert.NotEqual(1, IndexHealth.ExitDegraded);
    }
}
