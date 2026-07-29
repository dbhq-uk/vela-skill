using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public static class Schema
{
    /// <summary>
    /// The shape of the database this build reads and writes, stamped into every index
    /// it creates and checked before any index is queried.
    ///
    /// The index is a cache, and it is opened by whatever build of vela happens to be
    /// on the PATH. Adding document.generated therefore turned every cache built before
    /// it into a raw "SqliteException: no such column: d.generated" from every verb,
    /// which tells the user nothing about what to do and is exactly the shape of
    /// failure Constraint 3 exists to forbid: the index cannot be read, so it must say
    /// so, in words, rather than as a stack trace or as a partial answer.
    ///
    /// 0 is what an unstamped database reads, which is every index built before this
    /// existed, so it can never be a valid version. 1 was the schema without the
    /// generated column. 2 adds it. 3 adds external_document. 4 adds
    /// occurrence.scip_symbol. A future change bumps this and nothing else: there is no
    /// migration, because re-indexing takes seconds and rebuilds from the truth rather
    /// than from a guess about what the old rows meant.
    /// </summary>
    public const int Version = 4;

    /// <summary>
    /// The version stamped on a database, or 0 for one built before vela stamped them.
    /// </summary>
    public static int ReadVersion(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static void Create(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            -- generated: this document came from a source-generated tree whose
            -- occurrences did not map back to a view, so the path is real to the
            -- compiler and absent from the disk. The Razor generator writes nothing
            -- unless EmitCompilerGeneratedFiles is set, so refs on a Razor app was
            -- answering with paths the reader could not open. refs and impact exclude
            -- these by default and declare what they suppressed; def and outline keep
            -- them, because for some Razor members the generated document holds the
            -- only definition there is.
            CREATE TABLE IF NOT EXISTS document (
                id           INTEGER PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                language      TEXT NOT NULL,
                generated     INTEGER NOT NULL DEFAULT 0
            );

            -- Two names for one symbol, and they are not interchangeable.
            --
            -- symbol is the Roslyn display string, ScentVerdict.Data.Entities.Perfume.Status.
            -- It is what a person or an agent types and reads, what every query matches
            -- against, what the whole-dotted-segment rule operates on and what the
            -- ambiguity tally groups by. Every one of those was measured and hardened on
            -- a real solution, so this column is the one the query layer uses and the
            -- only one it uses.
            --
            -- scip_symbol is the SCIP moniker for the same thing:
            -- scip-dotnet nuget ScentVerdict.Data 1.0.0.0 ScentVerdict/Data/Entities/Perfume#Status.
            -- It is what makes the index exportable and what lets an index somebody
            -- else's tool produced be correlated with this one. It is a different
            -- grammar answering a different question, so it is stored beside the display
            -- name rather than in place of it.
            CREATE TABLE IF NOT EXISTS occurrence (
                id            INTEGER PRIMARY KEY,
                document_id   INTEGER NOT NULL REFERENCES document(id),
                symbol        TEXT NOT NULL,
                scip_symbol   TEXT NOT NULL DEFAULT '',
                is_definition INTEGER NOT NULL,
                start_line    INTEGER NOT NULL,
                start_char    INTEGER NOT NULL,
                enc_end_line  INTEGER,
                enc_end_char  INTEGER
            );

            CREATE INDEX IF NOT EXISTS ix_occurrence_symbol ON occurrence(symbol);
            CREATE INDEX IF NOT EXISTS ix_occurrence_document ON occurrence(document_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS symbol_fts USING fts5(symbol);

            -- The files this index deliberately does not hold: source contributed from
            -- the NuGet package cache or from the .NET installation, which cannot sit
            -- under project_root and is nobody's first-party code. Not a gap, so not in
            -- index_health, but recorded rather than counted and discarded: `vela index`
            -- printed a number and threw the paths away, leaving nothing to check if the
            -- classification was ever wrong about a file.
            CREATE TABLE IF NOT EXISTS external_document (
                path TEXT NOT NULL
            );

            -- Constraint 3: an index that could not be built completely says so.
            CREATE TABLE IF NOT EXISTS index_health (
                built_at_utc TEXT NOT NULL,
                git_ref      TEXT,
                degraded     INTEGER NOT NULL,
                detail       TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // Stamped last, so a database that failed part way through the DDL is left
        // unstamped and is rejected on open rather than trusted. PRAGMA takes no
        // parameter binding, hence the interpolation of a private int constant.
        using var stamp = db.CreateCommand();
        stamp.CommandText = $"PRAGMA user_version = {Version}";
        stamp.ExecuteNonQuery();
    }
}
