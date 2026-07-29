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
    ///
    /// Occurrences in generated documents are excluded unless
    /// <paramref name="includeGenerated"/> says otherwise. The Razor generator's output
    /// is compiled but not written to disk, so those hits name paths the reader cannot
    /// open, and on a scaffolded Razor app they were the majority of the answer. They
    /// are never dropped silently: the caller asks
    /// <see cref="CountInGeneratedCode"/> for what was left out and says so
    /// (Constraint 3).
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern, bool includeGenerated = false)
        => QueryHelper.Select(db, $"""
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition, d.generated
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE {QueryHelper.SymbolMatches("o.symbol")}
              {(includeGenerated ? "" : "AND d.generated = 0")}
            ORDER BY d.relative_path, o.start_line
            """, symbolPattern);

    /// <summary>How many occurrences the default answer left out, and why it must say so.</summary>
    public static int CountInGeneratedCode(SqliteConnection db, string symbolPattern)
        => QueryHelper.Count(db, $"""
            SELECT COUNT(*)
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE {QueryHelper.SymbolMatches("o.symbol")}
              AND d.generated = 1
            """, symbolPattern);

    /// <summary>
    /// Why refs came back empty. refs matches occurrences directly, so two absences
    /// print the same "0 result(s)": no occurrence of that name was indexed at all, and
    /// every occurrence of it was suppressed for living in generated code.
    ///
    /// The second used to print the first, which put two contradictory sentences in one
    /// answer: "nothing of that name was indexed" above "3 further result(s) in
    /// generated code". It is reachable on the case vela exists for, a Razor page member
    /// whose only declaration is in the generator's output, and the reader acts on the
    /// first sentence.
    ///
    /// The test is <see cref="CountInGeneratedCode"/> itself, the same number the
    /// caller prints in the footer, so the explanation and the footer cannot disagree
    /// however either of them changes. It is exact rather than approximate: an empty
    /// default answer means no occurrence outside generated code exists, so a non-zero
    /// count here means every occurrence there is was suppressed. With
    /// --include-generated nothing is suppressed, so an empty answer scores zero here
    /// and correctly reports a symbol that is genuinely absent.
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string symbolPattern)
        => CountInGeneratedCode(db, symbolPattern) > 0
            ? QueryHelper.OnlyInGeneratedCode(symbolPattern)
            : QueryHelper.NoSuchSymbol(symbolPattern);
}
