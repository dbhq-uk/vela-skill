using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public static class Schema
{
    public static void Create(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS document (
                id           INTEGER PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                language      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS occurrence (
                id            INTEGER PRIMARY KEY,
                document_id   INTEGER NOT NULL REFERENCES document(id),
                symbol        TEXT NOT NULL,
                is_definition INTEGER NOT NULL,
                start_line    INTEGER NOT NULL,
                start_char    INTEGER NOT NULL,
                enc_end_line  INTEGER,
                enc_end_char  INTEGER
            );

            CREATE INDEX IF NOT EXISTS ix_occurrence_symbol ON occurrence(symbol);
            CREATE INDEX IF NOT EXISTS ix_occurrence_document ON occurrence(document_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS symbol_fts USING fts5(symbol);

            -- Constraint 4: an index that could not be built completely says so.
            CREATE TABLE IF NOT EXISTS index_health (
                built_at_utc TEXT NOT NULL,
                git_ref      TEXT,
                degraded     INTEGER NOT NULL,
                detail       TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
