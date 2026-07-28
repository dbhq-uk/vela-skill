using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public static class ScipLoader
{
    /// <summary>
    /// Loads a freshly emitted SCIP index into the database. This is a one-shot bulk
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
    public static void Load(SqliteConnection db, Scip.Index index)
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
            "INSERT INTO document(relative_path, language) VALUES ($p, $l) RETURNING id";
        insertDoc.Parameters.Add("$p", SqliteType.Text);
        insertDoc.Parameters.Add("$l", SqliteType.Text);

        using var insertOcc = db.CreateCommand();
        insertOcc.Transaction = tx;
        insertOcc.CommandText = """
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char)
            VALUES ($d, $s, $def, $sl, $sc, $el, $ec)
            """;
        foreach (var name in new[] { "$d", "$s", "$def", "$sl", "$sc", "$el", "$ec" })
            insertOcc.Parameters.Add(name, SqliteType.Integer);
        insertOcc.Parameters["$s"].SqliteType = SqliteType.Text;

        using var insertFts = db.CreateCommand();
        insertFts.Transaction = tx;
        insertFts.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ($s)";
        insertFts.Parameters.Add("$s", SqliteType.Text);

        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var doc in index.Documents)
        {
            insertDoc.Parameters["$p"].Value = doc.RelativePath;
            insertDoc.Parameters["$l"].Value = doc.Language;
            var docId = Convert.ToInt64(insertDoc.ExecuteScalar());

            foreach (var occ in doc.Occurrences)
            {
                var isDef = (occ.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0;
                insertOcc.Parameters["$d"].Value = docId;
                insertOcc.Parameters["$s"].Value = occ.Symbol;
                insertOcc.Parameters["$def"].Value = isDef ? 1 : 0;
                insertOcc.Parameters["$sl"].Value = occ.Range.Count > 0 ? occ.Range[0] : 0;
                insertOcc.Parameters["$sc"].Value = occ.Range.Count > 1 ? occ.Range[1] : 0;
                insertOcc.Parameters["$el"].Value =
                    occ.EnclosingRange.Count > 2 ? occ.EnclosingRange[2] : (object)DBNull.Value;
                insertOcc.Parameters["$ec"].Value =
                    occ.EnclosingRange.Count > 3 ? occ.EnclosingRange[3] : (object)DBNull.Value;
                insertOcc.ExecuteNonQuery();

                if (seenSymbols.Add(occ.Symbol))
                {
                    insertFts.Parameters["$s"].Value = occ.Symbol;
                    insertFts.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
    }
}
