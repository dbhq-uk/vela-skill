using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class DefQuery
{
    /// <summary>
    /// Where a symbol is defined. Same whole-segment matching as refs.
    ///
    /// Generated documents are always included here, unlike in refs and impact. For
    /// several Razor page members the generated document holds the only definition
    /// anywhere in the index, so excluding it would leave def with nothing to say about
    /// a symbol vela can see perfectly well. The path is marked (generated) on the way
    /// out so the reader knows it cannot be opened.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.Select(db, $"""
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition, d.generated
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE o.is_definition = 1
              AND {QueryHelper.SymbolMatches("o.symbol")}
            ORDER BY d.relative_path, o.start_line
            """, symbolPattern);

    /// <summary>
    /// Why def came back empty. Two different absences print the same "0 result(s)":
    /// a symbol vela never indexed, and a symbol vela knows only as a reference
    /// because it is declared outside this solution. Saying "no such symbol" for the
    /// second would be false.
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string symbolPattern)
        => QueryHelper.AnySymbolOccurrence(db, symbolPattern)
            ? $"Symbols matching '{symbolPattern}' occur in the index, but none of them is defined in it. "
              + "The declaration is most likely outside this solution, in a referenced assembly or package."
            : QueryHelper.NoSuchSymbol(symbolPattern);
}
