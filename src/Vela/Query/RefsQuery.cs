using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class RefsQuery
{
    /// <summary>
    /// Every occurrence of a symbol, definitions included.
    ///
    /// The pattern matches a whole dotted segment of the stored symbol name, so
    /// "Perfume.Status" and "Status" both find "App.Models.Perfume.Status" and neither
    /// finds "App.Models.HttpStatus". See <see cref="QueryHelper.SymbolMatches"/> for
    /// why that is spelled out rather than left to LIKE.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.Select(db, $"""
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE {QueryHelper.SymbolMatches("o.symbol")}
            ORDER BY d.relative_path, o.start_line
            """, symbolPattern);

    /// <summary>
    /// Why refs came back empty. refs matches occurrences directly, so no hits means
    /// no occurrence of that name was indexed at all, and the honest answer names
    /// the index rather than the code.
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string symbolPattern)
        => QueryHelper.NoSuchSymbol(symbolPattern);
}
