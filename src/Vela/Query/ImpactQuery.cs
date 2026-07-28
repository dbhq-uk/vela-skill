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
    /// Containment is tested on (line, character) pairs, not on lines. Both halves of
    /// a stored enclosing range carry a character, and a line-granular test says a
    /// reference is inside every definition that merely shares a line with it. C#
    /// permits several members on one line, and generated Razor emits a great deal of
    /// code that way, so "class C { void A(){ Helper.Do(); } void B(){} }" attributed
    /// the call in A to B: not noise beside the right answer, but a single confident
    /// wrong one. A named caller invites no second look, so a line-granular bound is
    /// the more damaging of the two errors.
    ///
    /// Innermost is then the candidate that opens last. Definitions that all contain
    /// the same point nest, so of two of them the one that opens later is the one
    /// inside the other, and comparing (start_line, start_char) descending settles
    /// same-line nesting as well: the method opening at character 10 wins over the
    /// type opening at character 0. This replaces the line-span key the previous
    /// version led with, which could not see inside a line at all.
    ///
    /// The remaining keys exist so that the same query over the same index answers
    /// identically on every run and every machine (Constraint 1), rather than however
    /// SQLite happens to break a tie: the earlier end (enc_end_line, enc_end_char) for
    /// two ranges that also open at the same place, then the symbol name, then the
    /// occurrence id, which is unique and so always settles it. Every key is an exact
    /// stored value; nothing here is scored, ranked or guessed.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.SelectBySymbolSuffix(db, """
            WITH target AS (
                SELECT o.id, o.document_id, o.start_line, o.start_char
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
                           ORDER BY caller.start_line DESC,
                                    caller.start_char DESC,
                                    caller.enc_end_line ASC,
                                    caller.enc_end_char ASC,
                                    caller.symbol ASC,
                                    caller.id ASC
                       ) AS depth
                FROM target
                JOIN occurrence caller
                  ON caller.document_id = target.document_id
                 AND caller.is_definition = 1
                 AND caller.enc_end_line IS NOT NULL
                 AND caller.enc_end_char IS NOT NULL
                 AND (target.start_line > caller.start_line
                      OR (target.start_line = caller.start_line
                          AND target.start_char >= caller.start_char))
                 AND (target.start_line < caller.enc_end_line
                      OR (target.start_line = caller.enc_end_line
                          AND target.start_char <= caller.enc_end_char))
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
