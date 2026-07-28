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
    /// </summary>
    public static string Render(IReadOnlyList<Hit> hits, HealthRecord health)
    {
        var sb = new StringBuilder();

        sb.Append(RenderBanner(health));

        // Ordinal ordering, so the same index answers the same question the same way
        // on every machine regardless of the current culture (Constraint 1).
        foreach (var group in hits.GroupBy(h => h.RelativePath).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(group.Key);
            foreach (var hit in group.OrderBy(h => h.Line).ThenBy(h => h.Character))
                sb.AppendLine($"  {hit.Line + 1,6}:{hit.Character + 1,-4} {(hit.IsDefinition ? "def" : "ref")}  {hit.Symbol}");
        }

        sb.AppendLine();
        sb.AppendLine($"{hits.Count} result(s)");
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
