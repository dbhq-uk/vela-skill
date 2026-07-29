using System.Text;
using Vela.Indexing;

namespace Vela.Query;

public static class OutputWriter
{
    /// <summary>
    /// Renders for a context window: grouped by file, one line per hit, and a
    /// loud banner when the index cannot be trusted to be complete.
    ///
    /// Hit positions are stored as Roslyn produced them, which is zero-based, and
    /// are converted here to the one-based line and column every editor shows.
    ///
    /// <paramref name="emptyExplanation"/> is printed only when there are no hits,
    /// and says which absence this is: nothing to report, or nothing indexed to
    /// report on. Callers pass the explanation their verb computed; null keeps the
    /// bare count, which is the right output when the caller cannot tell.
    ///
    /// <paramref name="symbolPattern"/> is passed only by the verbs whose hits are
    /// occurrences of a symbol pattern, which is refs and def, and turns on the
    /// ambiguity block. outline passes null because its argument is a file path and
    /// every file defines several symbols, so the notice would fire on every outline
    /// ever run. impact passes null too: its hits name the callers rather than the
    /// symbol asked about, so a tally read off them would name the wrong symbols
    /// entirely. It renders <see cref="Ambiguity.RenderCallers"/> itself instead.
    /// </summary>
    public static string Render(IReadOnlyList<Hit> hits, HealthRecord health,
                                string? emptyExplanation = null, string? symbolPattern = null)
    {
        var sb = new StringBuilder();

        sb.Append(RenderBanner(health));

        // Ordinal ordering, so the same index answers the same question the same way
        // on every machine regardless of the current culture (Constraint 1).
        foreach (var group in hits.GroupBy(h => h.RelativePath).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Marked on the file rather than on every line: the property belongs to the
            // document, and the reader needs it before they try to open the path.
            var generated = group.Any(h => h.IsGenerated);
            sb.AppendLine(generated ? group.Key + "  (generated)" : group.Key);

            foreach (var hit in group.OrderBy(h => h.Line).ThenBy(h => h.Character))
                sb.AppendLine($"  {hit.Line + 1,6}:{hit.Character + 1,-4} {(hit.IsDefinition ? "def" : "ref")}  {hit.Symbol}");
        }

        sb.AppendLine();
        sb.AppendLine($"{hits.Count} result(s)");

        // A marker nobody can interpret is not a warning. def and outline report
        // generated documents deliberately, so the one line that explains what the
        // marker means travels with them.
        if (hits.Any(h => h.IsGenerated))
            sb.AppendLine("(generated) marks source-generated code, which is not written to disk: "
                        + "the path is real to the compiler but you cannot open it.");

        // A pattern matches a whole dotted segment, so a bare name matches every symbol
        // whose last segment is that name, and the count above can describe four
        // different things at once. Nothing is filtered: the block only says what the
        // total spans. It prints nothing when the pattern resolved to one symbol.
        if (symbolPattern is not null)
            sb.Append(Ambiguity.RenderOccurrences(symbolPattern, Ambiguity.Of(hits)));

        // "0 result(s)" on its own reads as an authoritative "there is nothing
        // here". It is the sentence an agent acts on, so when it is printed it says
        // what it is an answer to.
        if (hits.Count == 0 && !string.IsNullOrWhiteSpace(emptyExplanation))
            sb.AppendLine(emptyExplanation);

        return sb.ToString();
    }

    /// <summary>
    /// The degraded-index banner, or an empty string when the index is healthy.
    ///
    /// Constraint 3: an incomplete index must never look like a complete one. The
    /// reading that does real damage is the empty result, because "this symbol is
    /// unused" and "I could not see the code that uses it" print identically, so the
    /// banner says outright that a short answer proves nothing.
    /// </summary>
    public static string RenderBanner(HealthRecord health)
    {
        if (!health.Degraded) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("!! INCOMPLETE INDEX - these results may be missing references.");
        if (!string.IsNullOrEmpty(health.Detail)) sb.AppendLine("   " + health.Detail);
        sb.AppendLine("   Do not treat an empty or short result as proof the symbol is unused.");
        sb.AppendLine();
        return sb.ToString();
    }
}
