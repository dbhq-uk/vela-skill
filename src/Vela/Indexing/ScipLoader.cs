using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public static class ScipLoader
{
    public static void Load(SqliteConnection db, Scip.Index index)
    {
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
