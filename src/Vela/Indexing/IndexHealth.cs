using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public record HealthRecord(DateTime BuiltAtUtc, string? GitRef, bool Degraded, string? Detail);

public static class IndexHealth
{
    /// <summary>Exit code for an answer produced from a degraded or stale index.</summary>
    public const int ExitDegraded = 3;

    public static void Write(SqliteConnection db, HealthRecord record)
    {
        // DELETE then INSERT must be atomic: without an explicit transaction, SQLite
        // commits each statement in a multi-statement command text separately, which
        // would leave a window with zero rows for a concurrent reader to observe.
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM index_health;
            INSERT INTO index_health(built_at_utc, git_ref, degraded, detail)
            VALUES ($b, $g, $d, $t);
            """;
        cmd.Parameters.AddWithValue("$b", record.BuiltAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$g", (object?)record.GitRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", record.Degraded ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", (object?)record.Detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public static HealthRecord Read(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT built_at_utc, git_ref, degraded, detail FROM index_health LIMIT 1";
        using var reader = cmd.ExecuteReader();

        // No health row at all is the most dangerous case: it means we do not even
        // know whether the index build reported success. Never treat that as healthy.
        if (!reader.Read())
            return new HealthRecord(DateTime.MinValue, null, Degraded: true, "index has no health record");

        // RoundtripKind is required so the "O" format string's UTC marker is honoured
        // by the parser. Plain DateTime.Parse would return a Local or Unspecified
        // kind here, silently shifting every timestamp that flows from this record.
        var builtAtUtc = DateTime.Parse(
            reader.GetString(0),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        return new HealthRecord(
            builtAtUtc,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
