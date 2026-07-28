using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class DefQuery
{
    /// <summary>Where a symbol is defined. Same suffix matching as refs.</summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.SelectBySymbolSuffix(db, """
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE o.is_definition = 1
              AND (o.symbol LIKE '%' || $s ESCAPE '\' OR o.symbol LIKE '%' || $s || '(%' ESCAPE '\')
            ORDER BY d.relative_path, o.start_line
            """, symbolPattern);
}
