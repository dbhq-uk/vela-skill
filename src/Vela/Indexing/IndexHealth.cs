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
        // Write leaves exactly zero or one row, but the schema has no primary key,
        // UNIQUE or CHECK constraint enforcing that singleton (adding one would
        // complicate the DELETE-then-INSERT that Write relies on for atomicity).
        // So a second row could in principle arrive by some other route: manual
        // SQL, a future caller that bypasses Write, a schema migration. Count the
        // rows up front so that case can never be mistaken for a normal read.
        using var countCmd = db.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM index_health";
        var rowCount = (long)countCmd.ExecuteScalar()!;

        // Order by built_at_utc so that, in the single-row case (the only case
        // Write produces), the most recently built record is the one considered.
        // "O" format timestamps are fixed-width, zero-padded and share a common
        // suffix for a given DateTimeKind, so ordering them as text matches
        // ordering them chronologically.
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT built_at_utc, git_ref, degraded, detail FROM index_health ORDER BY built_at_utc DESC LIMIT 1";
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
        var gitRef = reader.IsDBNull(1) ? null : reader.GetString(1);

        // More than one row means the health table is in a state Write never
        // produces: nobody can say which row reflects the real state of the index.
        // Rather than guess a winner (which could surface a stale healthy row while
        // a degraded one sits unread, exactly the failure this record exists to
        // prevent), report degraded outright. This is stricter than merely
        // preferring a degraded row over a healthy one, because it also covers the
        // case where every extra row happens to claim healthy.
        if (rowCount > 1)
            return new HealthRecord(builtAtUtc, gitRef, Degraded: true, $"index health table holds {rowCount} records, expected exactly one");

        return new HealthRecord(
            builtAtUtc,
            gitRef,
            reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
