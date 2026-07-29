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
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern, bool includeGenerated = false)
        => QueryHelper.Select(db, Sql(
            includeGenerated ? "" : "AND d.generated = 0",
            "SELECT d.relative_path, ranked.start_line, ranked.start_char, ranked.symbol, 1, d.generated"),
            symbolPattern);

    /// <summary>
    /// How many callers the default answer left out because they live in generated
    /// code. A blast radius that quietly shrinks is worse than one that is too large,
    /// so the caller prints this rather than letting the shorter list stand alone
    /// (Constraint 3).
    /// </summary>
    public static int CountInGeneratedCode(SqliteConnection db, string symbolPattern)
        => QueryHelper.Count(db,
            "SELECT COUNT(*) FROM (" +
            Sql("AND d.generated = 1", "SELECT d.relative_path, ranked.symbol, ranked.start_line, ranked.start_char") +
            ")", symbolPattern);

    /// <summary>
    /// The distinct symbols the pattern matched, with the number of references to each
    /// that impact went looking for callers of.
    ///
    /// impact answers with callers, so unlike refs and def it cannot tell the reader
    /// the pattern was ambiguous by tallying its own rows: those rows name the calling
    /// symbols, and the symbols asked about appear nowhere in the answer. The tally has
    /// to be read from the index instead, and it counts the references the pattern
    /// matched rather than the callers found. Those are different numbers on purpose. A
    /// caller containing three references to the target is one row above, a caller that
    /// uses two of the matched symbols is still one row, and a reference in a Razor view
    /// or a top level statement falls inside no recorded body range and so yields no row
    /// at all. Per-symbol caller counts would therefore not decompose the total, and the
    /// block would fail the check that makes it worth printing.
    /// <see cref="Ambiguity.RenderCallers"/> labels the number it prints as what it is.
    ///
    /// The document filter mirrors <see cref="Run"/> so the two describe the same view of
    /// the index: a target is only reachable through a caller in the same document, so
    /// suppressing generated callers suppresses exactly these references too.
    /// </summary>
    public static IReadOnlyList<SymbolTally> MatchedSymbols(
        SqliteConnection db, string symbolPattern, bool includeGenerated = false)
        => QueryHelper.Tally(db, $"""
            SELECT o.symbol, COUNT(*)
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE o.is_definition = 0
              AND {QueryHelper.SymbolMatches("o.symbol")}
              {(includeGenerated ? "" : "AND d.generated = 0")}
            GROUP BY o.symbol
            """, symbolPattern);

    private static string Sql(string documentFilter, string projection)
        => $"""
            WITH target AS (
                SELECT o.id, o.document_id, o.start_line, o.start_char
                FROM occurrence o
                WHERE o.is_definition = 0
                  AND {QueryHelper.SymbolMatches("o.symbol")}
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
            {projection}
            FROM ranked
            JOIN document d ON d.id = ranked.document_id
            WHERE ranked.depth = 1
              {documentFilter}
            GROUP BY d.relative_path, ranked.symbol, ranked.start_line, ranked.start_char
            ORDER BY d.relative_path, ranked.start_line
            """;

    /// <summary>
    /// Why impact came back empty. Five different absences print the same
    /// "0 result(s)": every caller was suppressed for living in generated code, the
    /// symbol was never indexed, the symbol is known only from generated code, nothing
    /// refers to it, and references that sit where no enclosing definition was recorded,
    /// which is the normal case for top level statements and Razor views. Only the
    /// fourth is anything like "nothing calls it".
    ///
    /// The first two of those are new here. This used to reason over unfiltered
    /// occurrences and references while the answer itself was filtered, so a symbol
    /// whose whole existence is in the Razor generator's output was reported as
    /// "nothing of that name was indexed" directly above "1 further result(s) in
    /// generated code", and a symbol whose callers were all generated was blamed on
    /// missing body ranges. The order matters: the generated-caller test comes first
    /// because it is the strongest thing that can be said, and it is driven by
    /// <see cref="CountInGeneratedCode"/>, the same number the caller prints in the
    /// footer, so the two cannot disagree.
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string symbolPattern)
    {
        if (CountInGeneratedCode(db, symbolPattern) > 0)
            return $"Every caller of '{symbolPattern}' that vela can name is in source-generated code, "
                 + "which the compiler builds from Razor and never writes to disk, so the default answer "
                 + "left all of them out. The symbol is in the index and it does have callers: this empty "
                 + "result is not evidence that nothing calls it. Pass --include-generated to see them.";

        if (!QueryHelper.AnySymbolOccurrence(db, symbolPattern))
            return QueryHelper.NoSuchSymbol(symbolPattern);

        // Reached when the symbol exists but nothing about it is on disk, so the
        // remaining explanations, which are all about what impact could see, would be
        // reasoning from a filtered view while claiming to describe the whole index.
        if (!QueryHelper.AnySymbolOccurrenceOnDisk(db, symbolPattern))
            return QueryHelper.OnlyInGeneratedCode(symbolPattern);

        if (!QueryHelper.AnySymbolReference(db, symbolPattern))
            return $"'{symbolPattern}' is in the index, and no reference to it is recorded, so there is no "
                 + "call site to attribute to a caller. Run refs to see the occurrences themselves.";

        return $"References to '{symbolPattern}' are in the index, but none of them falls inside a definition "
             + "whose body range was recorded, so no calling symbol can be named. Top level statements and "
             + "Razor views have no recorded body range. This empty result is not evidence that nothing calls it; "
             + "run refs to see the references themselves.";
    }
}
