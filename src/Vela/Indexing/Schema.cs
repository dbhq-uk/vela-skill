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
    /// occurrence.scip_symbol. 5 adds document.position_encoding and an index on
    /// occurrence.scip_symbol. 6 adds import_health. A future change bumps this and
    /// nothing else: there is no migration, because re-indexing takes seconds and
    /// rebuilds from the truth rather than from a guess about what the old rows meant.
    /// </summary>
    public const int Version = 6;

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
            --
            -- position_encoding: the Scip.PositionEncoding the index this document came
            -- from declared for it. It is NOT how to read the numbers in the occurrence
            -- table. Every start_char and enc_end_char in this database is a count of
            -- UTF-16 code units, whatever the source said, because that is what Roslyn
            -- produces and what every row vela has ever written already meant.
            --
            -- It is recorded because the conversion is otherwise unauditable and an
            -- export cannot put a document back the way its indexer wrote it. scip.proto
            -- tells an indexer to pick by implementation language - UTF-16 for .NET and
            -- TypeScript, UTF-32 for Python, UTF-8 for Go, Rust and C++ - so a polyglot
            -- index legitimately holds documents counted three different ways, and 0
            -- (UnspecifiedPositionEncoding) is what a real indexer writes when it
            -- declares nothing: scip-typescript 0.4.0 leaves the field unset on every
            -- document it emits.
            CREATE TABLE IF NOT EXISTS document (
                id           INTEGER PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                language      TEXT NOT NULL,
                generated     INTEGER NOT NULL DEFAULT 0,
                position_encoding INTEGER NOT NULL DEFAULT 0
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
            --
            -- Two values in this column are sentinels and not monikers, and a join that
            -- forgets either fuses unrelated symbols:
            --
            --   '' means THIS OCCURRENCE HAS NO MONIKER, not "the empty moniker".
            --   scip.proto makes Occurrence.symbol optional, and vela leaves it empty
            --   rather than claim a document scope an array type or the global namespace
            --   does not have. 23,200 occurrences of the real solution (2.48%) carry it,
            --   so any join on this column needs `AND scip_symbol <> ''` or it makes one
            --   equivalence class of all 23,200.
            --
            --   'local <id>' is scoped to ONE document. scip.proto: local symbols "MUST
            --   only be used for entities which are local to a Document, and cannot be
            --   accessed from outside the Document". The ids are counters, so `local 1`
            --   in two files is two unrelated things - four documents of a real
            --   scip-typescript index over ScentVerdict.Mobile each carry one. A join on
            --   this column must therefore also match document_id. The display name in
            --   the symbol column is namespaced by document and does not have this
            --   problem, which is why it is the column every query uses.
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

            -- The moniker is stored so two indexes can be correlated through it, and
            -- correlating means one lookup per symbol. Without this each lookup is a
            -- scan of every occurrence. Measured on the real solution's index, 935,029
            -- occurrences carrying 140,430 distinct monikers over a 277.9 MiB database:
            -- counting the occurrences of one moniker took 148.8ms unindexed and 0.01ms
            -- with this index, which SQLite then serves as a covering index. It costs
            -- 76.3 MiB, a 27% larger file, and 1.6s to build.
            --
            -- Deliberately NOT a partial index on `scip_symbol <> ''`, which would have
            -- excluded the 23,200 sentinel rows and cost less. SQLite only uses a
            -- partial index when the query's WHERE clause provably implies the index's,
            -- and a bound parameter proves nothing, so `WHERE scip_symbol = $x AND
            -- scip_symbol <> ''` went back to a full scan. An index the obvious query
            -- cannot use is worse than no index, because it costs the space anyway.
            CREATE INDEX IF NOT EXISTS ix_occurrence_scip_symbol ON occurrence(scip_symbol);

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

            -- Constraint 3: an index that could not be built completely says so. This
            -- row is the INDEXING PASS's verdict on itself and nobody else's: it is
            -- written by `vela index` and never touched by an import, so built_at_utc
            -- keeps meaning "when this code was last compared against the disk", which
            -- is what the freshness check measures from.
            CREATE TABLE IF NOT EXISTS index_health (
                built_at_utc TEXT NOT NULL,
                git_ref      TEXT,
                degraded     INTEGER NOT NULL,
                detail       TEXT
            );

            -- One row per imported .scip whose LAST import left code out of the index,
            -- keyed by the file it came from. Presence is the degradation: a source
            -- imported cleanly has no row here.
            --
            -- Health is contributed by source because it was not, and a real user paid
            -- for it. `vela import` used to write `degraded = existing.degraded OR
            -- mine` into index_health and append its detail to whatever was there, so
            -- nothing could ever clear it and every import made the banner longer. A
            -- cache in the wild sat at degraded=1 with four duplicate-document entries
            -- from an import that had since succeeded, and printed "!! The index is
            -- INCOMPLETE" above every answer it gave. Crying wolf is the failure
            -- Constraint 3 cuts both ways on: a banner that is wrong is a banner nobody
            -- reads by the time it is right.
            --
            -- source is the PRIMARY KEY, so importing the same file again REPLACES its
            -- contribution rather than adding a second one, and there is no state in
            -- which two rows describe one source and nobody can say which is current.
            CREATE TABLE IF NOT EXISTS import_health (
                source          TEXT NOT NULL PRIMARY KEY,
                imported_at_utc TEXT NOT NULL,
                detail          TEXT NOT NULL
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
