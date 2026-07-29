using System.Text;

namespace Vela.Query;

/// <summary>
/// One distinct symbol that a pattern matched, and how many of something belong to it.
/// What that something is depends on the verb, and the block that prints the tally says
/// so outright rather than leaving the reader to infer it.
///
/// <see cref="Constructions"/> is how many raw, exact stored names <see cref="Ordered"/>
/// merged to produce this row. It defaults to 1, which is what every tally is before
/// merging: one exact stored name is one construction of itself. A generic type or
/// method instantiated several times, or a member reached through several constructions
/// of the type declaring it, merges into a row whose Constructions is more than one, and
/// that is the number the ambiguity block reports separately from the count of distinct
/// symbols, so that 271 constructions of one type are never read as 271 things.
/// </summary>
public record SymbolTally(string Symbol, int Count, int Constructions = 1);

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
    /// The tallies merged by <see cref="QueryHelper.WithoutTypeArguments"/>, with most
    /// hits first and ties broken by symbol name ordinally.
    ///
    /// The merge exists because a bare name matches a symbol through its type argument
    /// list (<see cref="QueryHelper.Matches"/>), so a raw tally grouped by the exact
    /// stored name has one row per construction of a generic type or method rather than
    /// one row per symbol: on the real solution `refs ILogger` tallied 271 rows for 271
    /// constructions of the one type ILogger&lt;T&gt;.
    ///
    /// The key is <see cref="QueryHelper.WithoutTypeArguments"/> and not something
    /// narrower, because that is the reading of a name <see cref="QueryHelper.Matches"/>
    /// itself uses. Two symbols that share a key are two symbols no pattern can select
    /// between, so a tally that kept them apart would count things a reader cannot ask
    /// about separately and would offer a narrowing suggestion that resolves to nothing.
    /// Removing only the declaring segment's own type arguments was tried and was not
    /// enough: a member of a constructed generic carries the arguments in an EARLIER
    /// segment, so `refs Count` on the real index reported "2573 result(s) above span
    /// 556 distinct symbols" - 332 of them List&lt;System.String&gt;.Count and 224
    /// List&lt;System.Guid&gt;.Count - and printed the narrowing sentence with no
    /// example, because no candidate resolved. 390 such keys are in that index.
    ///
    /// <see cref="SymbolTally.Constructions"/> keeps the raw count so the block can
    /// still say how many stored names were merged.
    ///
    /// The cost is stated rather than hidden: two overloads that differ only inside a
    /// type argument list, Find(List&lt;Perfume&gt;) and Find(List&lt;Note&gt;), merge
    /// into one row. They are also two symbols no pattern distinguishes, which is the
    /// same rule, and their construction count survives. Overloads that differ anywhere
    /// else - Publish(Perfume) and Publish(Note) - stay apart, because the parameter
    /// list itself is kept.
    ///
    /// Both keys used for the final ordering are needed for it to be total, and it is
    /// applied here rather than in SQL for the same reason every other ordering in vela
    /// is: an ORDER BY that leaves ties unbroken is settled by whatever the query plan
    /// produced, so the same index would answer the same question differently on two
    /// machines (Constraint 1). Ordinal, not culture-aware, so the answer does not
    /// depend on the current locale.
    /// </summary>
    public static IReadOnlyList<SymbolTally> Ordered(IEnumerable<SymbolTally> tallies) =>
        tallies.GroupBy(tally => QueryHelper.WithoutTypeArguments(tally.Symbol), StringComparer.Ordinal)
               .Select(group => new SymbolTally(
                   group.Key,
                   group.Sum(tally => tally.Count),
                   group.Sum(tally => tally.Constructions)))
               .OrderByDescending(tally => tally.Count)
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
          + $"{DistinctSymbolsSpan(tally)}:");

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
                ? $"the callers above are those of {DistinctSymbolsSpan(tally)} together, not of one."
                : $"the empty answer above is about {DistinctSymbolsSpan(tally)} together, not about one.")
          + " The number beside each symbol below is how many references to it vela searched for "
          + "callers of, not how many callers it has:");

    /// <summary>
    /// "N distinct symbols", with "across M construction(s)" appended when the tally
    /// merged more than one raw stored name into at least one of its rows.
    ///
    /// The clause is left out otherwise (no-crying-wolf): most patterns are ambiguous
    /// between symbols that were never constructed differently at all, and printing
    /// "4 distinct symbols across 4 constructions" on every one of those would train
    /// the reader to stop reading the clause by the time a pattern like `refs ILogger`
    /// needed it to say 271 instead of 4.
    /// </summary>
    private static string DistinctSymbolsSpan(IReadOnlyList<SymbolTally> tally)
    {
        var constructions = tally.Sum(entry => entry.Constructions);
        return constructions > tally.Count
            ? $"{tally.Count} distinct symbols across {constructions} construction(s) of them"
            : $"{tally.Count} distinct symbols";
    }

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
    ///
    /// The segments are taken from <see cref="QueryHelper.PlainName"/> and not from the
    /// stored name, because the stored name is not made of dotted segments where a type
    /// argument sits: splitting 'App.Logging.ILogger&lt;App.Services.PerfumeService&gt;'
    /// on '.' offers 'PerfumeService&gt;' and 'ILogger&lt;App' as names to type back in.
    /// </summary>
    private static string HowToNarrowIt(IReadOnlyList<SymbolTally> tally)
    {
        var target = tally[0].Symbol;
        var segments = QueryHelper.PlainName(target).Split('.');

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
