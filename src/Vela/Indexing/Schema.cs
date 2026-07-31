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
    /// occurrence.scip_symbol. 6 adds import_health. 7 adds document.source and
    /// imported_source. 8 adds project_input and its two child tables, the record of what
    /// each project was built from. 9 adds project_note and project_document, which are
    /// what let a project be SKIPPED without its problems and its documents being
    /// forgotten, and index_health.rebuild. 10 adds index_identity. A future change bumps
    /// this and nothing else: there is no migration, because re-indexing takes seconds and
    /// rebuilds from the truth rather than from a guess about what the old rows meant.
    /// </summary>
    public const int Version = 10;

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
            --
            -- source: WHERE THIS DOCUMENT CAME FROM, and the one value in it that is a
            -- sentinel rather than a path:
            --
            --   '' means vela's own Roslyn harvest wrote this row. It is not "an unknown
            --   source" and not "a .scip with no name": it is the harvest, which has no
            --   file to point at because it read the compilation rather than a file.
            --
            -- Anything else is the absolute path of the .scip an import read, and it is
            -- the same key that row's entry in imported_source and import_health carries.
            --
            -- It exists because without it nothing in the database could say that an
            -- imported document was imported, and `vela index` therefore had nothing to
            -- replay. Measured live: a cache rebuilt one morning held 2,205 csharp
            -- documents, 307 razor and zero typescript, because a proven scip-typescript
            -- import had been wiped by a re-index - silently, at exit 0, with
            -- index_health.degraded = 0 and import_health empty. A whole language had
            -- vanished from an index that called itself complete, which is exactly what
            -- Constraint 3 forbids.
            CREATE TABLE IF NOT EXISTS document (
                id           INTEGER PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                language      TEXT NOT NULL,
                generated     INTEGER NOT NULL DEFAULT 0,
                position_encoding INTEGER NOT NULL DEFAULT 0,
                source        TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS ix_document_source ON document(source);

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

            -- The files this index does not hold, named rather than counted. Two kinds
            -- reach it, and whether their absence is a GAP is recorded elsewhere:
            --
            --   `vela index`: source contributed from the NuGet package cache or from
            --   the .NET installation, which cannot sit under project_root and is
            --   nobody's first-party code. Not a gap, so deliberately not in
            --   index_health.
            --
            --   `vela import`: a document of an imported .scip whose file lies outside
            --   the tree vela is indexing. That IS a gap, and it degrades the index
            --   through import_health as well as being named here.
            --
            -- Recorded rather than counted and discarded, in both cases for the same
            -- reason: `vela index` printed a number and threw the paths away, and the
            -- import put them only in the health detail, which is summarised past ten
            -- entries. Both left nothing to check if the classification was ever wrong
            -- about a file, and a truncated string is not a record.
            CREATE TABLE IF NOT EXISTS external_document (
                path TEXT NOT NULL
            );

            -- Constraint 3: an index that could not be built completely says so. This
            -- row is the INDEXING PASS's verdict on itself and nobody else's: it is
            -- written by `vela index` and never touched by an import, so built_at_utc
            -- keeps meaning "when this code was last compared against the disk", which
            -- is what the freshness check measures from.
            --
            -- rebuild says HOW this index was last built, and it is NULL for a full
            -- rebuild. An incremental run rebuilds some projects and reuses the rows of
            -- others, so "when was this index built" stops being one fact: the reused
            -- rows are as old as the run that last wrote them, and project_input.built_at
            -- says how old. This column names which projects were which, so a reader who
            -- distrusts an answer can see whether the project it came from was looked at.
            CREATE TABLE IF NOT EXISTS index_health (
                built_at_utc TEXT NOT NULL,
                git_ref      TEXT,
                degraded     INTEGER NOT NULL,
                detail       TEXT,
                rebuild      TEXT
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

            -- Every .scip that has ever been imported into this index, whether or not it
            -- went cleanly. This is the table that makes an import survive a rebuild.
            --
            -- import_health holds only the imports that LOST something, because presence
            -- there is the degradation. That is the wrong record to replay from: an
            -- import that went perfectly leaves no row, and a perfect import is exactly
            -- the one a rebuild must not throw away. So the two are separate tables
            -- keyed the same way - source is the absolute path of the .scip - and this
            -- one records every import there has ever been.
            --
            -- `vela index` reads this table BEFORE it deletes the database, and replays
            -- each file into the index it builds. content_hash is what makes the replay
            -- honest: a file that has changed since is re-imported from what is on disk
            -- now and SAID to have changed, rather than reported as though the index
            -- still held what was imported. A file that is gone, or that will not read,
            -- degrades the index and names itself, because the alternative is the
            -- silence this whole table exists to end.
            --
            -- documents and occurrences are what that import contributed, so a replay
            -- that comes back smaller is a fact a reader can check rather than one
            -- nobody can see.
            CREATE TABLE IF NOT EXISTS imported_source (
                source          TEXT NOT NULL PRIMARY KEY,
                imported_at_utc TEXT NOT NULL,
                content_hash    TEXT NOT NULL,
                documents       INTEGER NOT NULL,
                occurrences     INTEGER NOT NULL
            );

            -- WHAT EACH PROJECT WAS BUILT FROM. Nothing recorded this, so nothing could
            -- check whether a project's code had moved on since the index was built, and
            -- an incremental rebuild would have had to guess.
            --
            -- A full rebuild cannot be stale, because it reads everything. An incremental
            -- one is a CLAIM that what it skipped has not changed, and if the claim is
            -- wrong the index holds rows describing code that no longer exists, at line
            -- numbers that have moved, while reporting itself complete. That is
            -- Constraint 3's exact failure and it is worse than the slowness it replaces.
            -- These three tables are what make the claim checkable.
            --
            -- project is the identity: the project file relative to the root the index is
            -- built at, so it is the same string in any checkout on any machine. A
            -- multi-targeted project is several compilations over one file and carries
            -- its Roslyn name after a '#', because one row describing two compilations
            -- would be wrong about at least one of them.
            --
            -- fingerprint is a SHA-256 over every input, taken from CONTENT and never
            -- from modification times. index_health's freshness check compares mtimes and
            -- is right to: it runs on every query and only has to raise a suspicion. A
            -- decision about what NOT to re-read is a different question, and an mtime
            -- changes when nothing did and stands still when something did.
            --
            -- schema_version and vela_version sit on the ROW rather than beside the table
            -- because they are facts about the run that wrote it, and an incremental
            -- rebuild writes some rows and leaves others. One value elsewhere would be
            -- the version of the most recent run, which says nothing about the rows that
            -- run did not touch.
            CREATE TABLE IF NOT EXISTS project_input (
                project        TEXT NOT NULL PRIMARY KEY,
                name           TEXT NOT NULL,
                fingerprint    TEXT NOT NULL,
                inputs         INTEGER NOT NULL,
                schema_version INTEGER NOT NULL,
                vela_version   TEXT NOT NULL,
                built_at_utc   TEXT NOT NULL
            );

            -- The inputs themselves, one row each, which is what makes a rebuild decision
            -- auditable rather than merely made. The digest can say a project changed and
            -- never which file did it.
            --
            -- kind is part of the key because one path can honestly arrive twice - an
            -- .editorconfig is both an analyzer-config document and a file on disk - and
            -- because a reader working out why a project rebuilt needs to know which
            -- channel named it. The kinds are on ProjectFingerprint; 'additional' is the
            -- one worth naming here, because that is where a .cshtml or .razor lives. A
            -- view never reaches the compiler as a file, so hashing only the compiled
            -- documents would have called a project unchanged after a view was rewritten.
            --
            -- content_hash is 64 hex characters, or 'not-read' for a reference whose path
            -- is the evidence, or 'unreadable' for a file that would not open. Neither
            -- sentinel can be confused with a hash.
            CREATE TABLE IF NOT EXISTS project_input_document (
                project      TEXT NOT NULL,
                kind         TEXT NOT NULL,
                path         TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                PRIMARY KEY (project, kind, path)
            );

            -- The project reference graph, which is the part a rebuild plan cannot work
            -- out from anything else in this database. A project is not independent:
            -- change a public member in one and every reference to it in the projects
            -- downstream moves, though not one of their files was touched. Getting that
            -- closure wrong is the silent-staleness failure, so the edges are stored
            -- rather than inferred.
            CREATE TABLE IF NOT EXISTS project_input_reference (
                project    TEXT NOT NULL,
                referenced TEXT NOT NULL,
                PRIMARY KEY (project, referenced)
            );

            -- EVERY REASON ONE PROJECT IS MISSING CODE FROM THIS INDEX, recorded against
            -- the project rather than against the run.
            --
            -- This is what closes the hole a fingerprint opens. A project that will not
            -- compile is fingerprinted like any other, so an incremental run can skip it -
            -- and its `compile-error:` note is produced fresh by each harvest and by
            -- nothing else, so skipping it meant the note was never regenerated. The index
            -- would stop calling itself degraded while still holding an incomplete picture
            -- of that project. A broken project that goes quiet is precisely the failure
            -- this tool exists to prevent.
            --
            -- Keeping the note when the project is skipped is honest for the same reason
            -- the skip is: the fingerprint says nothing this project compiles has changed,
            -- and the closure says nothing upstream of it has either, so its diagnostics
            -- have not changed. The alternative - rebuilding every project that has ever
            -- had a problem - pays full price for byte-identical output and makes the
            -- degraded index the one that never gets faster, which is backwards.
            --
            -- The text is the emitter's own note, prefix and all, so a reader sees exactly
            -- what a full rebuild would have said and Program classifies it by exactly the
            -- same prefixes.
            CREATE TABLE IF NOT EXISTS project_note (
                project TEXT NOT NULL,
                note    TEXT NOT NULL,
                PRIMARY KEY (project, note)
            );

            -- WHICH DOCUMENTS EACH PROJECT CONTRIBUTED TO, which is the other thing an
            -- incremental rebuild cannot work out from anything else here.
            --
            -- A document is keyed by the file a developer can open, and two projects can
            -- compile one file: a linked file, a shared source directory, a wildcard that
            -- reaches into a common folder. The occurrences of every project that compiles
            -- it land in ONE document row, so replacing that document on behalf of one
            -- project deletes the other's rows and nothing puts them back. The plan reads
            -- this table to pull every such project into the rebuild.
            --
            -- It is also how a rebuild knows what to DELETE. A file that has gone leaves a
            -- document nothing in the fresh harvest names, and without a record of what
            -- the project used to contribute there would be nothing to match it against.
            CREATE TABLE IF NOT EXISTS project_document (
                project       TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                PRIMARY KEY (project, relative_path)
            );

            -- WHICH SOLUTION THIS INDEX IS OF. Nothing recorded it, and the file name
            -- cannot: an index is named <SolutionName>-<hash>.db, where the hash is a
            -- SHA-256 of the absolute solution path, and a hash does not go backwards.
            --
            -- Two things needed it. `vela cache` could otherwise only show a reader a list
            -- of hashes and ask them to guess which of their checkouts each one was. And an
            -- index whose solution no longer exists is the one thing that can be evicted
            -- without any risk of surprising anybody - it describes a repository that is not
            -- there - which cannot be established without knowing what it was of.
            --
            -- The path is the one RealPath resolved, links followed and letter case read
            -- back on the platforms that have one, because that is the spelling every other
            -- part of vela keys on and two spellings of one solution would be two indexes.
            --
            -- One row, replaced whole, like index_health. An index is of one solution for
            -- as long as it exists, and if it were ever of two nobody could say which.
            CREATE TABLE IF NOT EXISTS index_identity (
                solution_path TEXT NOT NULL
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
