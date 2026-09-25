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
    /// The ambiguity block is deliberately not rendered here. It qualifies the result
    /// count, and so does the line naming what was suppressed for living in generated
    /// code, which only the caller knows; both belong after this, in that order, so
    /// that the shorter sentence stays beside the number it corrects.
    /// </summary>
    /// <param name="limit">
    /// The most hit lines to print, or 0 for all of them. An ordinary name answered with
    /// thousands of rows on a real solution, which floods a context window with an answer
    /// nobody reads to the end. The count below the hits is still the whole count, and a
    /// line says how many were not shown, so a total is never mistaken for the answer.
    /// The banner is above the hits and the ambiguity block is the caller's, after them,
    /// so neither is ever cut.
    /// </param>
    /// <param name="byFile">
    /// One line per file with the number of hits in it, instead of the hits: the shape
    /// of an answer too long to read, for deciding which files to ask about.
    /// </param>
    public static string Render(IReadOnlyList<Hit> hits, HealthRecord health,
                                string? emptyExplanation = null, int limit = 0, bool byFile = false)
    {
        var sb = new StringBuilder();

        sb.Append(RenderBanner(health));

        // Ordinal ordering, so the same index answers the same question the same way
        // on every machine regardless of the current culture (Constraint 1).
        var groups = hits.GroupBy(h => h.RelativePath).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var shown = new List<Hit>();

        foreach (var group in groups)
        {
            // Marked on the file rather than on every line: the property belongs to the
            // document, and the reader needs it before they try to open the path.
            var generated = group.Any(h => h.IsGenerated);
            var path = generated ? group.Key + "  (generated)" : group.Key;

            if (byFile)
            {
                sb.AppendLine($"  {group.Count(),6}  {path}");
                shown.AddRange(group);
                continue;
            }

            if (limit > 0 && shown.Count >= limit) break;
            sb.AppendLine(path);

            // A file-level hit has no line, so it says `file` where the line would go
            // rather than 1:1, which would send the reader to the top of the file looking
            // for a tag that is not there. It sorts first, being stored at position 0.
            foreach (var hit in group.OrderBy(h => h.Line).ThenBy(h => h.Character))
            {
                if (limit > 0 && shown.Count >= limit) break;

                var kind = hit.IsDefinition ? "def" : "ref";
                sb.AppendLine(hit.IsFileLevel
                    ? $"  {"file",6} {"",-4} {kind}  {hit.Symbol}"
                    : $"  {hit.Line + 1,6}:{hit.Character + 1,-4} {kind}  {hit.Symbol}");
                shown.Add(hit);
            }
        }

        sb.AppendLine();
        sb.AppendLine(byFile ? $"{hits.Count} result(s) in {groups.Count} file(s)" : $"{hits.Count} result(s)");

        // Said beside the count it qualifies. The count is the whole answer and the lines
        // above are not, and a reader sizing a change needs to know which one they read.
        if (!byFile && shown.Count < hits.Count)
            sb.AppendLine($"{hits.Count - shown.Count} more not shown: --limit {limit} cut the list. Raise "
                        + "--limit, or pass --files for a count per file.");

        if (!byFile && shown.Any(h => h.IsFileLevel))
            sb.AppendLine("file marks a use the compiler places in that file without a line: a Razor component's "
                        + "own definition, or a use of it by tag such as <Badge />. The file is exact; search it "
                        + "for the tag to find the line.");

        // A marker nobody can interpret is not a warning. def and outline report
        // generated documents deliberately, so the one line that explains what the
        // marker means travels with them.
        if (shown.Any(h => h.IsGenerated))
            sb.AppendLine("(generated) marks source-generated code, which is not written to disk: "
                        + "the path is real to the compiler but you cannot open it.");

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
