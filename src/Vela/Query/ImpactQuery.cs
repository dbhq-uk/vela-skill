using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class ImpactQuery
{
    /// <summary>
    /// Callers, derived from stored enclosing ranges: a reference to the target
    /// that falls inside another symbol's enclosing range is a call from it.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.SelectBySymbolSuffix(db, """
            SELECT d.relative_path, caller.start_line, caller.start_char, caller.symbol, 1
            FROM occurrence target
            JOIN document d ON d.id = target.document_id
            JOIN occurrence caller
              ON caller.document_id = target.document_id
             AND caller.is_definition = 1
             AND caller.enc_end_line IS NOT NULL
             AND target.start_line BETWEEN caller.start_line AND caller.enc_end_line
            WHERE target.is_definition = 0
              AND (target.symbol LIKE '%' || $s ESCAPE '\' OR target.symbol LIKE '%' || $s || '(%' ESCAPE '\')
            GROUP BY caller.symbol, d.relative_path, caller.start_line
            ORDER BY d.relative_path, caller.start_line
            """, symbolPattern);
}
