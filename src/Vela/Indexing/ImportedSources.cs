using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// One .scip that has been imported into this index, and what it contributed.
/// </summary>
/// <param name="Source">
/// The absolute path of the file. It is the key everywhere: a pending job in
/// <see cref="Config.PendingJob"/> is recorded under it, an import's health is recorded
/// under it, and every document that import wrote carries it in document.source. One
/// identity, so importing a file settles everything that was waiting on that file and
/// nothing else.
/// </param>
/// <param name="ContentHash">
/// SHA-256 of the file as it was when it was read, in lower-case hex. This is the only
/// thing that lets a replay tell "the same file again" from "a different file at the same
/// path", and the two have to be told apart out loud: an indexer is normally re-run over
/// changed code between one `vela index` and the next, so a replay reporting the numbers
/// from the earlier import would be reporting something that is no longer true.
/// </param>
public sealed record ImportedSource(
    string Source,
    DateTime ImportedAtUtc,
    string ContentHash,
    int Documents,
    int Occurrences);

/// <summary>
/// What a rebuild has to put back, and whether it could be asked at all.
/// </summary>
/// <param name="RecordUnavailable">
/// True when there was a database there and this table could not be read from it, which
/// is what an index built by a vela older than schema 7 looks like. It is reported rather
/// than passed over, because "nothing to replay" and "no way to know what to replay" are
/// different facts and only one of them is safe to be quiet about. It cannot be a
/// degradation: nothing here can say whether that index held an import at all.
/// </param>
public sealed record RememberedImports(IReadOnlyList<ImportedSource> Sources, bool RecordUnavailable)
{
    public static readonly RememberedImports None = new(Array.Empty<ImportedSource>(), false);
}

/// <summary>
/// The durable record of which .scip files this index was built from, which is what makes
/// an import an identity rather than an event.
///
/// <b>Why it exists.</b> `vela index` deletes the database and rebuilds it, because
/// <see cref="ScipLoader.Load"/> is a one-shot bulk load. Every imported language went
/// with it, and nothing anywhere recorded that there had been one, so there was nothing to
/// replay and nothing to warn about. Verified live rather than argued: the cache for the
/// repository vela is developed against was rebuilt one morning and held 2,205 csharp
/// documents, 307 razor and zero typescript. A proven scip-typescript import had been
/// wiped, at exit 0, with index_health.degraded = 0 and import_health empty. The index
/// called itself complete while a whole language had vanished from it.
///
/// <b>Why replay rather than a list to re-run by hand.</b> The brief allowed either: carry
/// the list across the rebuild and tell the user what to re-import, or replay it. Telling
/// the user leaves the index actually missing the language until somebody acts, so between
/// the rebuild and that action every answer is either incomplete or noisy, and `vela index`
/// stops being a command that produces a usable index. Replaying restores the state the
/// user already asked for, from files they already produced, and it costs nothing they have
/// not already paid: the .scip is on disk, the importer is the same code path, and anything
/// that cannot be replayed still degrades the index and still names the file. So the
/// weaker option is the fallback for exactly the cases where the stronger one is
/// impossible, which is the only place it is honest.
/// </summary>
public static class ImportedSources
{
    /// <summary>
    /// What the database at this path remembers being imported into it, read before that
    /// file is deleted and rebuilt.
    ///
    /// Tolerant on purpose. The index is a cache opened by whatever build of vela is on
    /// the PATH, so an older one with no such table is an ordinary thing to find, and it
    /// must not stop a rebuild - a verb that refused to index because it could not read
    /// the previous index would be worse than the problem. It is reported instead, through
    /// <see cref="RememberedImports.RecordUnavailable"/>.
    /// </summary>
    public static RememberedImports ReadFrom(string databasePath)
    {
        if (!File.Exists(databasePath)) return RememberedImports.None;

        try
        {
            // Pooling off for the same reason Program's connections have it off: this
            // database is about to be deleted, and a pooled connection kept alive over
            // that delete hands the next open a file nobody can see any more.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString();

            using var db = new SqliteConnection(connectionString);
            db.Open();
            return new RememberedImports(Read(db), false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new RememberedImports(Array.Empty<ImportedSource>(), true);
        }
    }

    /// <summary>
    /// Every import this database remembers, ordered by source so a rebuild replays them
    /// in the same order on every run and every machine (Constraint 1).
    /// </summary>
    /// <exception cref="SqliteException">The table is not there, which is what an index
    /// built before schema 7 looks like.</exception>
    public static IReadOnlyList<ImportedSource> Read(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT source, imported_at_utc, content_hash, documents, occurrences "
            + "FROM imported_source ORDER BY source";
        using var reader = cmd.ExecuteReader();

        var records = new List<ImportedSource>();
        while (reader.Read())
        {
            records.Add(new ImportedSource(
                reader.GetString(0),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return records;
    }

    /// <summary>
    /// Records an import, replacing whatever the same file recorded last time. Keyed by
    /// source, so there is no state in which two rows describe one file and nobody can say
    /// which is current - the same reason import_health is keyed that way.
    /// </summary>
    public static void Write(SqliteConnection db, ImportedSource record)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO imported_source(source, imported_at_utc, content_hash, documents, occurrences)
            VALUES ($s, $t, $h, $d, $o)
            ON CONFLICT(source) DO UPDATE SET
                imported_at_utc = excluded.imported_at_utc,
                content_hash    = excluded.content_hash,
                documents       = excluded.documents,
                occurrences     = excluded.occurrences
            """;
        cmd.Parameters.AddWithValue("$s", record.Source);
        cmd.Parameters.AddWithValue("$t", record.ImportedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$h", record.ContentHash);
        cmd.Parameters.AddWithValue("$d", record.Documents);
        cmd.Parameters.AddWithValue("$o", record.Occurrences);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// SHA-256 of a file, in lower-case hex. Streamed rather than read whole: a real
    /// .scip runs to tens of megabytes and this is asked once per import.
    /// </summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
