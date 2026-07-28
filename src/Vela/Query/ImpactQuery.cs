using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class ImpactQuery
{
    /// <summary>
    /// Callers, derived from stored enclosing ranges: a reference to the target
    /// that falls inside another symbol's enclosing range is a call from it.
    ///
    /// Only the innermost enclosing definition counts. Real C# nests, and the
    /// emitter stores an enclosing range for namespace and type declarations as
    /// well as for methods, so a single reference sits inside three ranges at once.
    /// Listing the namespace and the type beside the method would treble the answer
    /// with things that call nothing, and an agent reading a blast radius acts on
    /// every name in it.
    ///
    /// Innermost means the smallest line span, enc_end_line - start_line. Ties are
    /// broken here rather than left to the engine, because the same query over the
    /// same index must answer identically on every run and every machine
    /// (Constraint 1): first the candidate that starts latest, since of two ranges
    /// of equal height the later one is the more deeply nested; then the one that
    /// starts furthest right on that line; then the symbol name; and last the
    /// occurrence id, which is unique and so always settles it.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.SelectBySymbolSuffix(db, """
            WITH target AS (
                SELECT o.id, o.document_id, o.start_line
                FROM occurrence o
                WHERE o.is_definition = 0
                  AND (o.symbol LIKE '%' || $s ESCAPE '\' OR o.symbol LIKE '%' || $s || '(%' ESCAPE '\')
            ),
            ranked AS (
                SELECT target.id AS target_id,
                       caller.document_id AS document_id,
                       caller.symbol AS symbol,
                       caller.start_line AS start_line,
                       caller.start_char AS start_char,
                       ROW_NUMBER() OVER (
                           PARTITION BY target.id
                           ORDER BY caller.enc_end_line - caller.start_line ASC,
                                    caller.start_line DESC,
                                    caller.start_char DESC,
                                    caller.symbol ASC,
                                    caller.id ASC
                       ) AS depth
                FROM target
                JOIN occurrence caller
                  ON caller.document_id = target.document_id
                 AND caller.is_definition = 1
                 AND caller.enc_end_line IS NOT NULL
                 AND target.start_line BETWEEN caller.start_line AND caller.enc_end_line
            )
            SELECT d.relative_path, ranked.start_line, ranked.start_char, ranked.symbol, 1
            FROM ranked
            JOIN document d ON d.id = ranked.document_id
            WHERE ranked.depth = 1
            GROUP BY d.relative_path, ranked.symbol, ranked.start_line, ranked.start_char
            ORDER BY d.relative_path, ranked.start_line
            """, symbolPattern);

    /// <summary>
    /// Why impact came back empty. Three different absences print the same
    /// "0 result(s)": a symbol that was never indexed, a symbol nothing refers to,
    /// and references that sit where no enclosing definition was recorded, which is
    /// the normal case for top level statements and Razor views. Only the middle one
    /// is anything like "nothing calls it".
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string symbolPattern)
    {
        if (!QueryHelper.AnySymbolOccurrence(db, symbolPattern))
            return QueryHelper.NoSuchSymbol(symbolPattern);

        if (!QueryHelper.AnySymbolReference(db, symbolPattern))
            return $"'{symbolPattern}' is in the index, and no reference to it is recorded, so there is no "
                 + "call site to attribute to a caller. Run refs to see the occurrences themselves.";

        return $"References to '{symbolPattern}' are in the index, but none of them falls inside a definition "
             + "whose body range was recorded, so no calling symbol can be named. Top level statements and "
             + "Razor views have no recorded body range. This empty result is not evidence that nothing calls it; "
             + "run refs to see the references themselves.";
    }
}
