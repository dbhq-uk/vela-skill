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

        // Commands are prepared once outside the row loop, and each is bound to the
        // transaction explicitly: Microsoft.Data.Sqlite throws InvalidOperationException
        // ("Transaction is required...") on ExecuteNonQuery/ExecuteScalar if a command's
        // Transaction is null while a transaction is active on the connection.
        using var insertDoc = db.CreateCommand();
        insertDoc.Transaction = tx;
        insertDoc.CommandText =
            "INSERT INTO document(relative_path, language, generated, position_encoding) "
            + "VALUES ($p, $l, $g, $e) RETURNING id";
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

        using var insertFts = db.CreateCommand();
        insertFts.Transaction = tx;
        insertFts.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ($s)";
        insertFts.Parameters.Add("$s", SqliteType.Text);

        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var doc in index.Documents)
        {
            insertDoc.Parameters["$p"].Value = doc.RelativePath;
            insertDoc.Parameters["$l"].Value = doc.Language;
            insertDoc.Parameters["$g"].Value =
                generatedDocuments is not null && generatedDocuments.Contains(doc.RelativePath) ? 1 : 0;

            // Carried across as the index declares it and not normalised, because this
            // load is the emitter's own output and the emitter's offsets are already
            // UTF-16 code units. <see cref="ScipImporter"/> is the path that converts.
            insertDoc.Parameters["$e"].Value = (int)doc.PositionEncoding;
            var docId = Convert.ToInt64(insertDoc.ExecuteScalar());

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

                // The full-text index is what `find` searches, and `find` is a person
                // typing a name, so it holds display names.
                if (seenSymbols.Add(display))
                {
                    insertFts.Parameters["$s"].Value = display;
                    insertFts.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
    }
}
