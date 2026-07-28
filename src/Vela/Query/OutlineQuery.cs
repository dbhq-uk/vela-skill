using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class OutlineQuery
{
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string relativePath)
        => QueryHelper.Select(db, """
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE d.relative_path = $s AND o.is_definition = 1
            ORDER BY o.start_line
            """, relativePath);
}
