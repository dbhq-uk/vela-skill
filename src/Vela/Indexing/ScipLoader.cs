using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public static class ScipLoader
{
    /// <summary>
    /// Loads what the emitter just produced, with both of a symbol's names and the two
    /// facts SCIP has nowhere to put, straight off the <see cref="Harvest.EmitResult"/>
    /// so no caller has to remember to pass them.
    ///
    /// The empty-schema precondition of the overload below applies here unchanged.
    /// </summary>
    public static void Load(SqliteConnection db, Vela.Harvest.EmitResult emitted) =>
        Load(db, emitted.Index, emitted.GeneratedDocuments, emitted.DisplayNames);

    /// <summary>
    /// Loads a SCIP index into the database. This is a one-shot bulk
    /// load and requires an empty schema: a database that has just had
    /// <see cref="Schema.Create"/> called on it, or an equivalently empty one.
    /// <see cref="Schema.Create"/> uses "CREATE TABLE IF NOT EXISTS" and never
    /// truncates existing rows, so this is not an incremental update - re-running
    /// Load against a database that already holds a previous index (the normal
    /// re-index scenario) is a precondition violation, not a supported code path.
    /// Incremental updates are explicitly out of scope for now (YAGNI). Callers that
    /// are re-indexing must delete or recreate the database file before calling
    /// Load again.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown up front if the document table already contains rows, so the failure
    /// is intelligible rather than a raw SqliteException from the relative_path
    /// UNIQUE constraint partway through the load.
    /// </exception>
    /// <param name="generatedDocuments">
    /// Relative paths of documents the emitter produced from a source-generated tree
    /// that did not map back to a file on disk. SCIP has no field for this and the
    /// path alone is not evidence of it, so it is carried beside the index and stored
    /// in vela's own schema, where refs and impact read it.
    /// </param>
    /// <param name="displayNames">
    /// What vela calls each occurrence's symbol, which cannot be read off the index
    /// because Occurrence.symbol is the SCIP moniker. An occurrence with no entry keeps
    /// the moniker as its display name, which is what an index read from another tool's
    /// .scip file gets: their symbol is the only name it has.
    /// </param>
    public static void Load(
        SqliteConnection db,
        Scip.Index index,
        IReadOnlySet<string>? generatedDocuments = null,
        IReadOnlyDictionary<Scip.Occurrence, string>? displayNames = null)
    {
        using (var checkCmd = db.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM document";
            var existing = Convert.ToInt64(checkCmd.ExecuteScalar());
            if (existing > 0)
            {
                throw new InvalidOperationException(
                    "The index already contains data; delete the file and re-index. " +
                    "ScipLoader.Load requires an empty schema and does not support " +
                    "incremental updates.");
            }
        }

        using var tx = db.BeginTransaction();

        using var insertFts = db.CreateCommand();
        insertFts.Transaction = tx;
        insertFts.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ($s)";
        insertFts.Parameters.Add("$s", SqliteType.Text);

        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);

        InsertDocuments(db, tx, index, generatedDocuments, displayNames, null, display =>
        {
            // The full-text index is what `find` searches, and `find` is a person
            // typing a name, so it holds display names.
            if (!seenSymbols.Add(display)) return;
            insertFts.Parameters["$s"].Value = display;
            insertFts.ExecuteNonQuery();
        });

        tx.Commit();
    }

    /// <summary>
    /// What an incremental load did, in numbers a caller can print.
    /// </summary>
    /// <param name="SkippedPaths">
    /// Documents the harvest produced that were NOT written, because an import already
    /// holds that path. relative_path is UNIQUE, so writing one would abort the whole
    /// rebuild; the row is left where it is and the collision is named, because a document
    /// quietly not written is the shape of failure this codebase forbids.
    /// </param>
    public sealed record IncrementalLoad(
        int DocumentsRemoved, int DocumentsWritten, IReadOnlyList<string> SkippedPaths);

    /// <summary>
    /// Replaces the rows of the projects that were rebuilt, and leaves everything else
    /// exactly as it was.
    ///
    /// <b>What it deletes.</b> Every document named in <paramref name="replacedPaths"/> -
    /// what the rebuilt projects contributed to when the index was last built - together
    /// with every document the fresh harvest names. The first set is what makes a DELETED
    /// file disappear from the index: the harvest cannot name a file that is not there, so
    /// without the ledger's record there would be nothing to match the stale row against.
    ///
    /// <b>What it will not delete.</b> Anything an import contributed. A full rebuild
    /// deletes the database and replays every .scip into the new one; this never deletes
    /// the database, so an imported language survives by not being touched. That is why
    /// the deletes are restricted to source = '', which is vela's own harvest.
    ///
    /// <b>The full-text table is rebuilt whole.</b> symbol_fts is keyed to nothing - no
    /// document, no source - so a name whose last occurrence has gone survives every
    /// targeted delete anybody could write, and `find`, the verb an agent uses before
    /// deciding a name does not exist, would answer with symbols no occurrence carries.
    /// Rebuilding it from the occurrence table is exact by construction and covers
    /// imported symbols as well as harvested ones.
    ///
    /// All of it is one transaction. A rebuild that committed the deletes and failed
    /// before the inserts would leave an index missing whole files while reporting itself
    /// complete.
    /// </summary>
    public static IncrementalLoad LoadIncremental(
        SqliteConnection db, Vela.Harvest.EmitResult emitted, IReadOnlyCollection<string> replacedPaths)
    {
        var index = emitted.Index;

        var fresh = index.Documents
            .Select(document => document.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        var targets = new HashSet<string>(replacedPaths, StringComparer.Ordinal);
        targets.UnionWith(fresh);

        using var tx = db.BeginTransaction();

        var importOwned = ImportOwnedPaths(db, tx);
        var removed = 0;

        using (var deleteOccurrences = db.CreateCommand())
        using (var deleteDocument = db.CreateCommand())
        {
            deleteOccurrences.Transaction = tx;
            deleteOccurrences.CommandText =
                "DELETE FROM occurrence WHERE document_id IN "
                + "(SELECT id FROM document WHERE relative_path = $p AND source = '')";
            deleteOccurrences.Parameters.Add("$p", SqliteType.Text);

            deleteDocument.Transaction = tx;
            deleteDocument.CommandText = "DELETE FROM document WHERE relative_path = $p AND source = ''";
            deleteDocument.Parameters.Add("$p", SqliteType.Text);

            // Ordered, so two runs over the same tree do the same work in the same order
            // and a reader debugging one can follow it (Constraint 1).
            foreach (var path in targets.OrderBy(path => path, StringComparer.Ordinal))
            {
                deleteOccurrences.Parameters["$p"].Value = path;
                deleteOccurrences.ExecuteNonQuery();

                deleteDocument.Parameters["$p"].Value = path;
                removed += deleteDocument.ExecuteNonQuery();
            }
        }

        var written = InsertDocuments(
            db, tx, index, emitted.GeneratedDocuments, emitted.DisplayNames, importOwned, null);

        RebuildSymbolIndex(db, tx);

        tx.Commit();

        var skipped = fresh
            .Where(importOwned.Contains)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return new IncrementalLoad(removed, written, skipped);
    }

    /// <summary>
    /// The documents an import contributed, which vela's own harvest must neither delete
    /// nor write over.
    /// </summary>
    private static HashSet<string> ImportOwnedPaths(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT relative_path FROM document WHERE source <> ''";
        using var reader = cmd.ExecuteReader();

        var owned = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) owned.Add(reader.GetString(0));
        return owned;
    }

    /// <summary>
    /// symbol_fts, rebuilt from the occurrences that are actually there. Ordered, so the
    /// table is byte-identical for a given set of occurrences however they arrived.
    /// </summary>
    private static void RebuildSymbolIndex(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM symbol_fts;
            INSERT INTO symbol_fts(symbol) SELECT DISTINCT symbol FROM occurrence ORDER BY symbol;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The document and occurrence rows of one emitted index, shared by the one-shot load
    /// and the incremental one so neither can drift from the other about what a row means.
    /// </summary>
    /// <param name="skipPaths">Paths another source owns, which are left alone.</param>
    /// <param name="onSymbol">
    /// Called with every display name written, in order. The one-shot load uses it to fill
    /// symbol_fts as it goes; the incremental load rebuilds that table afterwards instead
    /// and passes null.
    /// </param>
    private static int InsertDocuments(
        SqliteConnection db,
        SqliteTransaction tx,
        Scip.Index index,
        IReadOnlySet<string>? generatedDocuments,
        IReadOnlyDictionary<Scip.Occurrence, string>? displayNames,
        IReadOnlySet<string>? skipPaths,
        Action<string>? onSymbol)
    {
        // Commands are prepared once outside the row loop, and each is bound to the
        // transaction explicitly: Microsoft.Data.Sqlite throws InvalidOperationException
        // ("Transaction is required...") on ExecuteNonQuery/ExecuteScalar if a command's
        // Transaction is null while a transaction is active on the connection.
        using var insertDoc = db.CreateCommand();
        insertDoc.Transaction = tx;

        // source is written as the empty sentinel explicitly rather than left to the
        // column default, because this is the one place in vela that produces it and the
        // reader should be able to see what it means here: '' is vela's own Roslyn
        // harvest, which has no file to point at. Every other value in that column is the
        // absolute path of a .scip an import read.
        insertDoc.CommandText =
            "INSERT INTO document(relative_path, language, generated, position_encoding, source) "
            + "VALUES ($p, $l, $g, $e, '') RETURNING id";
        insertDoc.Parameters.Add("$p", SqliteType.Text);
        insertDoc.Parameters.Add("$l", SqliteType.Text);
        insertDoc.Parameters.Add("$g", SqliteType.Integer);
        insertDoc.Parameters.Add("$e", SqliteType.Integer);

        using var insertOcc = db.CreateCommand();
        insertOcc.Transaction = tx;
        insertOcc.CommandText = """
            INSERT INTO occurrence(document_id, symbol, scip_symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char)
            VALUES ($d, $s, $scip, $def, $sl, $sc, $el, $ec)
            """;
        foreach (var name in new[] { "$d", "$s", "$scip", "$def", "$sl", "$sc", "$el", "$ec" })
            insertOcc.Parameters.Add(name, SqliteType.Integer);
        insertOcc.Parameters["$s"].SqliteType = SqliteType.Text;
        insertOcc.Parameters["$scip"].SqliteType = SqliteType.Text;

        var written = 0;

        foreach (var doc in index.Documents)
        {
            if (skipPaths is not null && skipPaths.Contains(doc.RelativePath)) continue;

            insertDoc.Parameters["$p"].Value = doc.RelativePath;
            insertDoc.Parameters["$l"].Value = doc.Language;
            insertDoc.Parameters["$g"].Value =
                generatedDocuments is not null && generatedDocuments.Contains(doc.RelativePath) ? 1 : 0;

            // Carried across as the index declares it and not normalised, because this
            // load is the emitter's own output and the emitter's offsets are already
            // UTF-16 code units. <see cref="ScipImporter"/> is the path that converts.
            insertDoc.Parameters["$e"].Value = (int)doc.PositionEncoding;
            var docId = Convert.ToInt64(insertDoc.ExecuteScalar());
            written++;

            foreach (var occ in doc.Occurrences)
            {
                var isDef = (occ.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0;
                var display = displayNames is not null && displayNames.TryGetValue(occ, out var known)
                    ? known
                    : occ.Symbol;

                insertOcc.Parameters["$d"].Value = docId;
                insertOcc.Parameters["$s"].Value = display;
                insertOcc.Parameters["$scip"].Value = occ.Symbol;
                insertOcc.Parameters["$def"].Value = isDef ? 1 : 0;
                insertOcc.Parameters["$sl"].Value = occ.Range.Count > 0 ? occ.Range[0] : 0;
                insertOcc.Parameters["$sc"].Value = occ.Range.Count > 1 ? occ.Range[1] : 0;
                insertOcc.Parameters["$el"].Value =
                    occ.EnclosingRange.Count > 2 ? occ.EnclosingRange[2] : (object)DBNull.Value;
                insertOcc.Parameters["$ec"].Value =
                    occ.EnclosingRange.Count > 3 ? occ.EnclosingRange[3] : (object)DBNull.Value;
                insertOcc.ExecuteNonQuery();

                onSymbol?.Invoke(display);
            }
        }

        return written;
    }
}
