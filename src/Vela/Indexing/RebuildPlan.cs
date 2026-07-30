namespace Vela.Indexing;

/// <summary>
/// Which projects a change actually invalidates, and why.
///
/// <b>Why this is its own thing, with its own tests.</b> A full rebuild cannot be stale,
/// because it reads everything. An incremental rebuild is a CLAIM that what it skipped has
/// not changed, and this is where the claim is made. Get it wrong in the generous
/// direction and time is wasted; get it wrong in the mean direction and the index holds
/// rows describing code that no longer exists, at line numbers that have moved, while
/// reporting itself complete. That is Constraint 3's exact failure and it is worse than
/// the slowness it replaces, so the decision is separated from the rebuilding and can be
/// tested without either.
///
/// <b>The hard part is the closure.</b> A project is not independent. Change a public
/// member in ScentVerdict.Data and every reference to it in ScentVerdict.Web moves, though
/// not one file in Web was touched. So the set is the changed projects PLUS everything
/// transitively downstream of them in the project reference graph. On the real solution
/// Data is upstream of every other project, so a change there rebuilds all ten. That is
/// correct, and it is the honest shape of the feature: incremental helps most when you
/// edit a leaf.
///
/// <b>Pure and deterministic.</b> No I/O, no clock, no environment. The same inputs give
/// the same set in the same order, because a plan is read by a person deciding whether to
/// trust it (Constraint 1).
/// </summary>
/// <param name="Rebuild">
/// The projects to rebuild, ordered ordinally. Empty means nothing changed, which is a
/// real and useful answer and not a failure to decide.
/// </param>
/// <param name="Reuse">
/// The projects whose rows can be kept, ordered ordinally. Stated rather than left to be
/// worked out by subtraction, because it is the half a reader has to be told: these are
/// the projects the index will go on answering about WITHOUT having looked at them.
/// </param>
/// <param name="RebuildsEverything">
/// True when a whole-index reason fired rather than a per-project one. Task 3 turns this
/// into a full rebuild, said out loud. A fallback is a good outcome and must never be
/// silent.
/// </param>
/// <param name="Reasons">
/// Why, in words, one sentence at a time and in a fixed order. Whole-index reasons first,
/// then one per selected project. Empty exactly when nothing was selected.
/// </param>
public sealed record RebuildPlan(
    IReadOnlyList<string> Rebuild,
    IReadOnlyList<string> Reuse,
    bool RebuildsEverything,
    IReadOnlyList<string> Reasons)
{
    /// <summary>Nothing to do: the tree and the ledger agree about every project.</summary>
    public bool RebuildsNothing => Rebuild.Count == 0;

    /// <summary>
    /// The plan for a tree, given what the index recorded last time it was built.
    /// </summary>
    /// <param name="current">
    /// Every project of the solution as it is now, fingerprinted. The reference edges come
    /// from HERE and not from the ledger, deliberately: the closure has to be taken over
    /// the graph that exists now. An edge that has since been deleted cannot propagate a
    /// change, and the project that deleted it changed its own project file, so it is
    /// selected in its own right anyway.
    /// </param>
    /// <param name="prior">What the ledger recorded, from <see cref="ProjectInputs.Read"/>.</param>
    /// <param name="schemaVersion">The schema this build of vela reads and writes.</param>
    /// <param name="velaVersion">This build of vela.</param>
    /// <param name="recordedDocuments">
    /// Which documents each project contributed to when the index was built, from
    /// <see cref="ProjectDocuments.Read"/>, or null to take no account of it.
    ///
    /// A document is keyed by the file a developer can open, and two projects can compile
    /// one file, so the occurrences of both land in ONE document row. Rebuilding one of
    /// them replaces that row and deletes the other's occurrences, and nothing puts them
    /// back. So a project that shares a document with a selected project is selected too,
    /// transitively.
    /// </param>
    public static RebuildPlan For(
        IReadOnlyList<ProjectFingerprint> current,
        IReadOnlyList<RecordedProject> prior,
        int schemaVersion,
        string velaVersion,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? recordedDocuments = null)
    {
        var everything = current
            .Select(project => project.Project)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToList();

        var wholeIndex = WholeIndexReasons(current, prior, schemaVersion, velaVersion, everything);
        if (wholeIndex.Count > 0)
            return new RebuildPlan(everything, Array.Empty<string>(), true, wholeIndex);

        var recorded = prior.ToDictionary(row => row.Project, row => row.Fingerprint, StringComparer.Ordinal);
        var known = everything.ToHashSet(StringComparer.Ordinal);

        // In identity order, because the seeds decide the order the closure discovers the
        // rest in, and therefore which upstream each downstream project is reported as
        // following from.
        var ordered = current
            .OrderBy(project => project.Project, StringComparer.Ordinal)
            .ToList();

        var seeds = new List<(string Project, string Reason)>();
        foreach (var project in ordered)
        {
            if (!recorded.TryGetValue(project.Project, out var was))
            {
                // Unreachable while the whole-index check above fires on any difference in
                // the project set, and kept because this function is pure and its inputs
                // come out of a database. A project the ledger has never heard of is one
                // nothing here knows anything about.
                seeds.Add((project.Project, project.Project + ": it has no recorded fingerprint, so nothing "
                                          + "in this index says what it was built from"));
                continue;
            }

            if (!string.Equals(was, project.Fingerprint, StringComparison.Ordinal))
            {
                seeds.Add((project.Project, project.Project + ": its own inputs changed"));
                continue;
            }

            // An edge pointing at a project this solution does not hold. Roslyn only
            // reports references it resolved, so this should not arise; if it does, an
            // upstream nobody can find is an upstream nobody can prove is unchanged.
            var missing = project.References
                .Where(reference => !known.Contains(reference))
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .FirstOrDefault();

            if (missing is not null)
            {
                seeds.Add((project.Project, project.Project + ": it references '" + missing
                                          + "', which is not a project in this solution, so nothing here can "
                                          + "say whether that code has changed"));
            }
        }

        var dependents = Dependents(ordered, known);
        var selected = Close(seeds, dependents);

        CloseOverSharedDocuments(selected, recordedDocuments, known);

        var rebuild = selected.Keys.OrderBy(identity => identity, StringComparer.Ordinal).ToList();
        var reuse = everything.Where(identity => !selected.ContainsKey(identity)).ToList();
        var reasons = rebuild.Select(identity => selected[identity]).ToList();

        return new RebuildPlan(rebuild, reuse, false, reasons);
    }

    /// <summary>
    /// The reasons that make the whole index unusable rather than one project's rows.
    ///
    /// All of them are collected rather than the first one returned, because two can be
    /// true at once - upgrading vela normally bumps the schema as well - and a reader owed
    /// an explanation is owed the whole of it.
    /// </summary>
    private static IReadOnlyList<string> WholeIndexReasons(
        IReadOnlyList<ProjectFingerprint> current,
        IReadOnlyList<RecordedProject> prior,
        int schemaVersion,
        string velaVersion,
        IReadOnlyList<string> everything)
    {
        var reasons = new List<string>();

        if (prior.Count == 0)
        {
            reasons.Add("no project in this index has a recorded fingerprint, so there is nothing to "
                      + "compare the tree against. This is what an index built before vela recorded "
                      + "what each project was built from looks like.");
            return reasons;
        }

        var schemas = prior
            .Select(row => row.SchemaVersion)
            .Where(version => version != schemaVersion)
            .Distinct()
            .OrderBy(version => version)
            .ToList();

        if (schemas.Count > 0)
        {
            reasons.Add($"the index schema changed: rows were written against version(s) "
                      + $"{string.Join(", ", schemas)} and this vela reads version {schemaVersion}, so what "
                      + "those rows mean is not what this build would write.");
        }

        var builds = prior
            .Select(row => row.VelaVersion)
            .Where(version => !string.Equals(version, velaVersion, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(version => version, StringComparer.Ordinal)
            .ToList();

        if (builds.Count > 0)
        {
            reasons.Add($"a different build of vela wrote this index: version(s) "
                      + $"{string.Join(", ", builds)} against this build's {velaVersion}. Two builds can "
                      + "emit different occurrences from identical source, so those rows are not evidence "
                      + "about what this one would produce.");
        }

        var recorded = prior.Select(row => row.Project).ToHashSet(StringComparer.Ordinal);
        var present = everything.ToHashSet(StringComparer.Ordinal);

        var added = everything.Where(identity => !recorded.Contains(identity)).ToList();
        var removed = prior
            .Select(row => row.Project)
            .Where(identity => !present.Contains(identity))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToList();

        if (added.Count > 0 || removed.Count > 0)
        {
            // Both directions, and both whole-index. A REMOVED project's documents are
            // still in the database and no per-project rebuild can reach them, because
            // there is no longer a project to name them. An ADDED one is the weaker case
            // and is treated the same way on purpose: the index is keyed by document and
            // two projects can compile the same file, so a new project can collide with
            // documents somebody else contributed. Anything that cannot be proved is
            // rebuilt.
            var parts = new List<string>();
            if (added.Count > 0) parts.Add("added " + string.Join(", ", added));
            if (removed.Count > 0) parts.Add("removed " + string.Join(", ", removed));

            reasons.Add("the set of projects changed since this index was built: " + string.Join("; ", parts)
                      + ". A project that has gone still has its documents in the index and no per-project "
                      + "rebuild can reach them, and a project that is new may contribute documents another "
                      + "project already did.");
        }

        return reasons;
    }

    /// <summary>
    /// The reverse of the reference graph: for each project, the projects that reference
    /// it and would therefore move if it changed.
    ///
    /// An edge to a project this solution does not hold is left out here and handled by
    /// the seeding instead, so a dangling edge selects the project that holds it rather
    /// than being quietly dropped.
    /// </summary>
    private static Dictionary<string, List<string>> Dependents(
        IReadOnlyList<ProjectFingerprint> ordered, HashSet<string> known)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var project in ordered)
        {
            foreach (var reference in project.References.OrderBy(r => r, StringComparer.Ordinal))
            {
                if (!known.Contains(reference)) continue;
                if (string.Equals(reference, project.Project, StringComparison.Ordinal)) continue;

                if (!dependents.TryGetValue(reference, out var downstream))
                    dependents[reference] = downstream = new List<string>();

                if (!downstream.Contains(project.Project, StringComparer.Ordinal))
                    downstream.Add(project.Project);
            }
        }

        return dependents;
    }

    /// <summary>
    /// Everything transitively downstream of the seeds, with the reason each project was
    /// reached by.
    ///
    /// A breadth-first walk over a membership set, so a project is added once however many
    /// routes reach it (a diamond) and a graph that contains a cycle terminates rather
    /// than hanging. A project reference graph should be acyclic and MSBuild refuses to
    /// build one that is not, but this walk is handed a graph read out of a database and
    /// hanging is the one failure the output cannot explain.
    /// </summary>
    private static Dictionary<string, string> Close(
        IReadOnlyList<(string Project, string Reason)> seeds,
        IReadOnlyDictionary<string, List<string>> dependents)
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        foreach (var seed in seeds)
        {
            if (selected.TryAdd(seed.Project, seed.Reason)) pending.Enqueue(seed.Project);
        }

        while (pending.Count > 0)
        {
            var upstream = pending.Dequeue();
            if (!dependents.TryGetValue(upstream, out var downstream)) continue;

            foreach (var project in downstream)
            {
                var reason = project + ": it is downstream of " + upstream
                           + ", so references it holds may have moved even though none of its own files did";

                if (selected.TryAdd(project, reason)) pending.Enqueue(project);
            }
        }

        return selected;
    }

    /// <summary>
    /// Adds every project that contributed to a document one of the selected projects
    /// also contributed to, transitively.
    ///
    /// <b>Why this is not optional.</b> A rebuild replaces a document whole, because a
    /// document is one row and its occurrences hang off it. Two projects compiling one
    /// file is ordinary - a linked file, a shared source directory, a wildcard reaching
    /// into a common folder - and both projects' occurrences land in that one row. So
    /// rebuilding one of them and not the other deletes the other's occurrences and writes
    /// nothing in their place: a file that is still on disk, still compiled, and half
    /// absent from an index that reports itself complete.
    ///
    /// The walk is over what the LEDGER recorded, which is what is actually in the
    /// database and therefore what is actually at risk. A file that has since moved from
    /// one project to another changed both projects' inputs, so both are already selected
    /// on their own account; what this catches is the case where only one of them changed.
    ///
    /// Everything is sorted before it is walked, so the same graph gives the same set in
    /// the same order with the same reasons, whichever order the projects arrived in
    /// (Constraint 1).
    /// </summary>
    private static void CloseOverSharedDocuments(
        Dictionary<string, string> selected,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? recordedDocuments,
        HashSet<string> known)
    {
        if (recordedDocuments is null || recordedDocuments.Count == 0) return;

        // Which projects contributed to each document, restricted to projects this
        // solution still holds: one the ledger remembers and the solution has lost cannot
        // be rebuilt, and the whole-index check above has already fired on it.
        var contributors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in recordedDocuments.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!known.Contains(entry.Key)) continue;

            foreach (var path in entry.Value.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!contributors.TryGetValue(path, out var projects))
                    contributors[path] = projects = new List<string>();
                projects.Add(entry.Key);
            }
        }

        var pending = new Queue<string>(selected.Keys.OrderBy(p => p, StringComparer.Ordinal));

        while (pending.Count > 0)
        {
            var project = pending.Dequeue();
            if (!recordedDocuments.TryGetValue(project, out var paths)) continue;

            foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!contributors.TryGetValue(path, out var sharing)) continue;

                foreach (var other in sharing)
                {
                    if (string.Equals(other, project, StringComparison.Ordinal)) continue;

                    var reason = other + ": it compiles " + path + ", which " + project
                               + " also compiles and which is being rebuilt. One document in this index "
                               + "holds the occurrences of every project that compiles it, so replacing "
                               + "it without this project would delete rows nothing would put back";

                    if (selected.TryAdd(other, reason)) pending.Enqueue(other);
                }
            }
        }
    }
}
