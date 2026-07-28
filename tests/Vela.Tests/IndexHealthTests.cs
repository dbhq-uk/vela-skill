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

    [Fact]
    public void Read_WhenMultipleRowsPresent_ReportsDegradedRegardlessOfRowOrder()
    {
        // Write always leaves zero or one row, but the schema has no singleton
        // constraint. If a second row ever arrives by some other route (manual SQL,
        // a future caller that bypasses Write, a migration), Read must not silently
        // pick a winner. Insert a healthy row first and a degraded row second, so a
        // naive "LIMIT 1" with no ORDER BY (which returns rows in rowid order) would
        // return the healthy row. Read must still report degraded.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO index_health(built_at_utc, git_ref, degraded, detail)
                VALUES ('2026-07-28T09:00:00.0000000Z', 'healthy-ref', 0, NULL);
                INSERT INTO index_health(built_at_utc, git_ref, degraded, detail)
                VALUES ('2026-07-28T08:00:00.0000000Z', 'degraded-ref', 1, 'App.csproj failed to load');
                """;
            cmd.ExecuteNonQuery();
        }

        var health = IndexHealth.Read(db);

        Assert.True(health.Degraded);
        Assert.NotNull(health.Detail);
        Assert.Contains("holds 2 records", health.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
