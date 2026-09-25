using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class ImplsQuery
{
    /// <summary>
    /// Every definition of a symbol that implements, overrides or derives from one matching
    /// the pattern: the classes that implement an interface or derive from a class, and the
    /// members that implement an interface member or override a member.
    ///
    /// Matched on the implemented symbol by the same whole-dotted-segment rule as refs, so
    /// `impls IShape` and `impls Shapes.IShape` both find what implements App.Shapes.IShape,
    /// and `impls IRepository` reaches every construction of a generic interface.
    ///
    /// Generated documents are included, as def includes them, and marked on the way out:
    /// the class a Razor page compiles to is only ever declared in one.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.Select(db, $"""
            SELECT DISTINCT d.relative_path, o.start_line, o.start_char, o.symbol, 1, d.generated, o.file_level
            FROM implementation i
            JOIN document d ON d.id = i.document_id
            JOIN occurrence o ON o.document_id = i.document_id AND o.symbol = i.symbol AND o.is_definition = 1
            WHERE {QueryHelper.SymbolMatches("i.implements")}
            ORDER BY d.relative_path, o.start_line, o.start_char, o.symbol
            """, symbolPattern);

    /// <summary>
    /// The implemented symbols the pattern matched, each with how many implementations of it
    /// the answer holds. More than one means the answer mixes the implementations of several
    /// symbols, and the ambiguity block says so.
    /// </summary>
    public static IReadOnlyList<SymbolTally> MatchedSymbols(
        SqliteConnection db, string symbolPattern, bool includeGenerated = true)
        => QueryHelper.Tally(db, $"""
            SELECT i.implements, COUNT(DISTINCT i.document_id || ' ' || i.symbol)
            FROM implementation i
            WHERE {QueryHelper.SymbolMatches("i.implements")}
            GROUP BY i.implements
            """, symbolPattern);

    /// <summary>
    /// Why impls came back empty. The index records implementations only from vela's own
    /// harvest of C# and Visual Basic, so for a symbol of an imported language, or one
    /// nothing in the solution declares an implementation of, the empty answer has to say
    /// which of those it is rather than read as "nothing implements this".
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string symbolPattern)
        => QueryHelper.AnySymbolOccurrence(db, symbolPattern)
            ? $"Symbols matching '{symbolPattern}' are in the index, and nothing vela harvested implements, "
              + "overrides or derives from them. Implementations are recorded for C# and Visual Basic only: "
              + "a language imported from a .scip records none, so for its symbols this empty answer is not "
              + "evidence."
            : QueryHelper.NoSuchSymbol(symbolPattern);
}
