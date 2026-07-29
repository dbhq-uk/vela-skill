using System.Text;

namespace Vela.Query;

/// <summary>
/// One distinct symbol that a pattern matched, and how many of something belong to it.
/// What that something is depends on the verb, and the block that prints the tally says
/// so outright rather than leaving the reader to infer it.
/// </summary>
public record SymbolTally(string Symbol, int Count);

/// <summary>
/// The block that says a pattern named more than one symbol.
///
/// A pattern matches a whole dotted segment, so a bare name matches every symbol whose
/// last segment is that name. Measured on a real 375,608-line solution, `vela refs
/// Perfume` answered 3,104 results, which is more than `grep -w Perfume` answers there,
/// because at least four distinct symbols end in that segment: the entity, the entity's
/// constructor, an enum member, and a property of an unrelated API response type.
///
/// Every one of those hits is real. The COUNT is what describes something that does not
/// exist, and the count is what an agent sizing a change reads. The whole claim of this
/// tool is that it replaces grep's noise with compiler-exact answers, so a headline
/// number silently spanning four symbols is the exact failure it exists to prevent.
///
/// This is therefore a reporting change and only a reporting change. The same rows come
/// back in the same order; suppressing them, ranking them or scoring them would be the
/// heuristic behaviour Constraint 1 forbids. The answer simply states what it spans.
///
/// It also has to stay quiet. A pattern that resolves to exactly one symbol prints
/// nothing at all, because a notice on every answer is a notice nobody reads, and this
/// project has already paid that price once with the degraded-index banner.
/// </summary>
public static class Ambiguity
{
    /// <summary>
    /// Symbols listed by name before the tail is summarised into one line.
    ///
    /// Measured on the real solution, `refs Name` matches 426 distinct symbols and
    /// `refs Id` matches 440, so listing every one puts the header sentence four hundred
    /// lines above the footer. The header is the whole point of the block, and a reader
    /// or an agent looking at the end of a long answer would see a list of one-hit
    /// symbols with nothing saying what it was. That is the degraded-index banner's
    /// mistake in a new place: a notice too long to read is a notice nobody reads.
    ///
    /// Ten, matching the limit the degraded-index detail already uses, and the tail is
    /// summarised rather than dropped, so the counts still add up to the reported total
    /// and the header still says how many symbols there really are. Nothing is hidden:
    /// the pattern is what shortens the answer, and the block says how to shorten it.
    /// </summary>
    private const int MaxSymbolsListed = 10;

    /// <summary>
    /// The distinct symbols a set of hits covers, with the number of hits of each.
    ///
    /// Correct only where the hits ARE occurrences of the pattern, which is refs and
    /// def. impact's hits name the callers instead, so it reads its tally from the index
    /// (<see cref="ImpactQuery.MatchedSymbols"/>) rather than from its own answer.
    /// </summary>
    public static IReadOnlyList<SymbolTally> Of(IEnumerable<Hit> hits) =>
        Ordered(hits.GroupBy(h => h.Symbol, StringComparer.Ordinal)
                    .Select(group => new SymbolTally(group.Key, group.Count())));

    /// <summary>
    /// Most hits first, ties broken by symbol name ordinally.
    ///
    /// Both keys are needed for the ordering to be total, and it is applied here rather
    /// than in SQL for the same reason every other ordering in vela is: an ORDER BY that
    /// leaves ties unbroken is settled by whatever the query plan produced, so the same
    /// index would answer the same question differently on two machines (Constraint 1).
    /// Ordinal, not culture-aware, so the answer does not depend on the current locale.
    /// </summary>
    public static IReadOnlyList<SymbolTally> Ordered(IEnumerable<SymbolTally> tallies) =>
        tallies.OrderByDescending(tally => tally.Count)
               .ThenBy(tally => tally.Symbol, StringComparer.Ordinal)
               .ToList();

    /// <summary>
    /// The block for a verb whose results are occurrences of the pattern: refs and def.
    /// The counts decompose the reported total exactly, which is what lets a reader
    /// check the block against the number it explains rather than weigh two confident
    /// numbers against each other.
    ///
    /// The sentence describes the results and not the index, because that is all the
    /// tally saw. refs leaves generated documents out by default, so a symbol whose
    /// every occurrence is in the Razor generator's output is not in these rows at all:
    /// on the real solution `refs Name` spans 426 symbols and
    /// `refs Name --include-generated` spans 427. "it matches N distinct symbols" was a
    /// claim about the whole index drawn from a filtered view of it, which is the exact
    /// failure this block exists to report.
    /// </summary>
    public static string RenderOccurrences(string pattern, IReadOnlyList<SymbolTally> tally) =>
        Render(tally,
            $"'{pattern}' is ambiguous: the {tally.Sum(entry => entry.Count)} result(s) above span "
          + $"{tally.Count} distinct symbols:");

    /// <summary>
    /// The block for impact, whose results are the callers and not the symbol asked
    /// about. A blast radius is the number an agent sizes a change from, so merging four
    /// symbols into one does more damage here than anywhere else.
    ///
    /// The numbers cannot be caller counts. impact reports each calling symbol once
    /// however many references it contains, so a caller that uses two of the matched
    /// symbols is one row and would be counted twice, and the tally would not decompose
    /// the total. They are the references the pattern matched, which is what impact went
    /// looking for callers of, and some of them yield no caller at all: a reference in a
    /// Razor view or in a top level statement sits inside no recorded body range. The
    /// sentence says exactly which of the two quantities the number is, so it cannot be
    /// read as the other.
    ///
    /// <paramref name="anyCallers"/> is false when impact named nobody. The block is
    /// printed then too, because an empty answer explained in the singular is still an
    /// answer about several symbols at once, and that explanation is the whole of it.
    /// </summary>
    public static string RenderCallers(string pattern, IReadOnlyList<SymbolTally> tally, bool anyCallers) =>
        Render(tally,
            $"'{pattern}' is ambiguous: "
          + (anyCallers
                ? $"the callers above are those of {tally.Count} distinct symbols together, not of one."
                : $"the empty answer above is about {tally.Count} distinct symbols together, not about one.")
          + " The number beside each symbol below is how many references to it vela searched for "
          + "callers of, not how many callers it has:");

    private static string Render(IReadOnlyList<SymbolTally> tally, string lead)
    {
        // One symbol is not ambiguous. This is the no-crying-wolf rule, and it matters
        // as much as the notice itself.
        if (tally.Count < 2) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine(lead);

        foreach (var entry in tally.Take(MaxSymbolsListed))
            sb.AppendLine($"  {entry.Count,6}  {entry.Symbol}");

        // The tail carries its own count, so the arithmetic the header promises still
        // holds when the list is shortened. Losing that would leave two numbers
        // describing one answer and no way to reconcile them.
        var remaining = tally.Skip(MaxSymbolsListed).ToList();
        if (remaining.Count > 0)
            sb.AppendLine($"  {remaining.Sum(entry => entry.Count),6}  (+{remaining.Count} further symbol(s))");

        sb.AppendLine(HowToNarrowIt(tally));
        return sb.ToString();
    }

    /// <summary>
    /// What to type instead. Naming the problem without naming the fix leaves the reader
    /// where they started, and the primary reader here is an agent, which will act on
    /// whatever pattern the sentence contains.
    ///
    /// The suggestion is the shortest trailing run of the biggest symbol's dotted
    /// segments that matches that symbol and none of the others, so it is derived from
    /// the names actually in the answer rather than guessed at. One segment is the
    /// pattern the caller already typed, so the search starts there and lengthens.
    ///
    /// A unique suffix need not exist: 'App.Models.Perfume' is a trailing run of
    /// 'Vendor.App.Models.Perfume', so no qualification of the first excludes the second.
    /// That case gets the rule without the promise, because claiming a pattern matches
    /// only one symbol when it matches two would be the failure this block exists to fix,
    /// committed by the sentence meant to fix it.
    /// </summary>
    private static string HowToNarrowIt(IReadOnlyList<SymbolTally> tally)
    {
        var target = tally[0].Symbol;
        var segments = QueryHelper.DottedName(target).Split('.');

        for (var take = 1; take <= segments.Length; take++)
        {
            var candidate = string.Join('.', segments[^take..]);
            if (tally.Count(entry => QueryHelper.Matches(entry.Symbol, candidate)) == 1)
            {
                return $"To ask about one of them, give more of its name: '{candidate}' matches {target} "
                     + "and none of the others. Any trailing run of a symbol's dotted segments matches "
                     + "that symbol, so lengthen the name until one symbol is left.";
            }
        }

        return "To ask about one of them, give more of its name. Any trailing run of a symbol's dotted "
             + "segments matches that symbol, so lengthen the name from the full names above until one "
             + "symbol is left.";
    }
}
