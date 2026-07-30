using Vela.Indexing;
using Xunit;

/// <summary>
/// Which projects a change actually invalidates.
///
/// This is the part of an incremental reindex that can be wrong silently. A plan that
/// selects too much costs time. A plan that selects too little leaves the index holding
/// rows about code that no longer exists, at line numbers that have moved, while
/// reporting itself complete - Constraint 3's exact failure, and worse than the slowness
/// it replaces.
///
/// The failure mode is the CLOSURE. A project is not independent: change a public member
/// in one and every reference to it in the projects downstream moves, though not one of
/// their own files was touched. So most of what follows is about the shapes a project
/// graph can take, because getting those wrong is what produces a stale index that looks
/// fresh.
/// </summary>
public class RebuildPlanTests
{
    private const string Vela = "1.2.3.4";
    private const int Schema = 8;

    [Fact]
    public void For_SelectsNothingWhenNothingChanged()
    {
        // The whole point. If an unchanged tree selects anything, the feature saves
        // nothing and nobody can tell a real change from noise.
        var current = new[]
        {
            Project("data", "aaa"),
            Project("web", "bbb", "data")
        };

        var plan = RebuildPlan.For(current, Prior(("data", "aaa"), ("web", "bbb")), Schema, Vela);

        Assert.Empty(plan.Rebuild);
        Assert.Equal(new[] { "data", "web" }, plan.Reuse);
        Assert.False(plan.RebuildsEverything);
        Assert.Empty(plan.Reasons);
    }

    [Fact]
    public void For_SelectsTheProjectWhoseOwnInputsChanged()
    {
        var current = new[]
        {
            Project("data", "aaa"),
            Project("tasks", "changed")
        };

        var plan = RebuildPlan.For(current, Prior(("data", "aaa"), ("tasks", "bbb")), Schema, Vela);

        Assert.Equal(new[] { "tasks" }, plan.Rebuild);
        Assert.Equal(new[] { "data" }, plan.Reuse);
        Assert.False(plan.RebuildsEverything);
    }

    [Fact]
    public void For_SelectsEverythingDownstreamOfAChangedProject()
    {
        // A chain three deep. Change the bottom and every project above it holds
        // references whose line numbers have moved, though none of their files did.
        var current = Chain();

        var plan = RebuildPlan.For(
            current, Prior(("a", "changed"), ("b", "b0"), ("c", "c0"), ("d", "d0")), Schema, Vela);

        Assert.Equal(new[] { "a", "b", "c", "d" }, plan.Rebuild);
        Assert.Empty(plan.Reuse);
    }

    [Fact]
    public void For_DoesNotSelectAnythingUpstreamOfTheChange()
    {
        // The other half, and the one that makes the feature worth having. A change at
        // the top of the chain cannot move anything below it, so nothing below it is
        // rebuilt. If this ever selected the whole graph the plan would be safe and
        // useless.
        var current = Chain();

        var plan = RebuildPlan.For(
            current, Prior(("a", "a0"), ("b", "b0"), ("c", "c0"), ("d", "changed")), Schema, Vela);

        Assert.Equal(new[] { "d" }, plan.Rebuild);
        Assert.Equal(new[] { "a", "b", "c" }, plan.Reuse);
    }

    [Fact]
    public void For_SelectsTheMiddleOfAChainAndEverythingAboveIt()
    {
        var current = Chain();

        var plan = RebuildPlan.For(
            current, Prior(("a", "a0"), ("b", "b0"), ("c", "changed"), ("d", "d0")), Schema, Vela);

        Assert.Equal(new[] { "c", "d" }, plan.Rebuild);
        Assert.Equal(new[] { "a", "b" }, plan.Reuse);
    }

    [Fact]
    public void For_ClosesOverADiamondWithoutNamingAnythingTwice()
    {
        // shared is reached from base by two different routes. A closure that appended
        // rather than checking membership would name it twice, and Task 3 deletes a
        // project's rows before writing them again: doing that twice inside one
        // transaction is at best wasted work and at worst a project whose rows are
        // deleted after they were written.
        var current = new[]
        {
            Project("base", "changed"),
            Project("left", "l0", "base"),
            Project("right", "r0", "base"),
            Project("shared", "s0", "left", "right")
        };

        var plan = RebuildPlan.For(
            current,
            Prior(("base", "b0"), ("left", "l0"), ("right", "r0"), ("shared", "s0")),
            Schema, Vela);

        Assert.Equal(new[] { "base", "left", "right", "shared" }, plan.Rebuild);
        Assert.Equal(plan.Rebuild.Count, plan.Rebuild.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void For_DoesNotHangOnACycle()
    {
        // A project graph should be acyclic and MSBuild refuses to build one that is not,
        // but this function is handed a graph read out of a database, and a walk that
        // trusted acyclicity would hang the whole command rather than produce a wrong
        // answer. Neither is acceptable, and hanging is the one that cannot be diagnosed
        // from the output.
        var current = new[]
        {
            Project("one", "changed", "two"),
            Project("two", "t0", "one"),
            Project("three", "th0", "two")
        };

        var plan = RebuildPlan.For(
            current, Prior(("one", "o0"), ("two", "t0"), ("three", "th0")), Schema, Vela);

        Assert.Equal(new[] { "one", "three", "two" }, plan.Rebuild);
    }

    [Fact]
    public void For_SelectsOnlyTheProjectWhenItIsUpstreamOfNothing()
    {
        // The case incremental exists for: a leaf that nothing references. On the real
        // solution this is where the whole saving lives.
        var current = new[]
        {
            Project("data", "d0"),
            Project("web", "w0", "data"),
            Project("tests", "changed", "web", "data")
        };

        var plan = RebuildPlan.For(
            current, Prior(("data", "d0"), ("web", "w0"), ("tests", "t0")), Schema, Vela);

        Assert.Equal(new[] { "tests" }, plan.Rebuild);
        Assert.Equal(new[] { "data", "web" }, plan.Reuse);
    }

    [Fact]
    public void For_SelectsEveryProjectWhenTheSchemaVersionChanged()
    {
        // The rows themselves mean something different, so none of them can be kept
        // whatever the source tree says.
        var current = new[] { Project("data", "d0"), Project("web", "w0", "data") };

        var plan = RebuildPlan.For(current, Prior(("data", "d0"), ("web", "w0")), Schema + 1, Vela);

        Assert.Equal(new[] { "data", "web" }, plan.Rebuild);
        Assert.True(plan.RebuildsEverything);
        Assert.Contains(plan.Reasons, r => r.Contains("schema", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void For_SelectsEveryProjectWhenTheVelaVersionChanged()
    {
        // A different build of vela can emit different occurrences from identical source:
        // the anchor rules, the dedup rules and the moniker grammar have each changed at
        // least once. Rows another build wrote are not evidence about what this one would
        // produce.
        var current = new[] { Project("data", "d0"), Project("web", "w0", "data") };

        var plan = RebuildPlan.For(current, Prior(("data", "d0"), ("web", "w0")), Schema, "9.9.9.9");

        Assert.Equal(new[] { "data", "web" }, plan.Rebuild);
        Assert.True(plan.RebuildsEverything);
        Assert.Contains(plan.Reasons, r => r.Contains("vela", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void For_SelectsEveryProjectWhenAProjectHasBeenRemoved()
    {
        // The removed project's documents are still in the index, and nothing in the plan
        // can delete rows for a project that is no longer there to name them. Only a
        // rebuild from nothing clears them.
        var current = new[] { Project("data", "d0") };

        var plan = RebuildPlan.For(current, Prior(("data", "d0"), ("web", "w0")), Schema, Vela);

        Assert.Equal(new[] { "data" }, plan.Rebuild);
        Assert.True(plan.RebuildsEverything);
        Assert.Contains(plan.Reasons, r => r.Contains("web", StringComparison.Ordinal));
    }

    [Fact]
    public void For_SelectsEveryProjectWhenOneOfThemHasNoPriorFingerprint()
    {
        // A project the ledger has never heard of. It may be new, or it may be one an
        // earlier run could not compile and therefore did not record. Either way nothing
        // here can prove anything about the rest of the solution's relationship to it, so
        // the whole set goes, and the reason names it.
        var current = new[] { Project("data", "d0"), Project("web", "w0", "data") };

        var plan = RebuildPlan.For(current, Prior(("data", "d0")), Schema, Vela);

        Assert.Equal(new[] { "data", "web" }, plan.Rebuild);
        Assert.True(plan.RebuildsEverything);
        Assert.Contains(plan.Reasons, r => r.Contains("web", StringComparison.Ordinal));
    }

    [Fact]
    public void For_SelectsEveryProjectWhenThereIsNoLedgerAtAll()
    {
        var current = new[] { Project("data", "d0"), Project("web", "w0", "data") };

        var plan = RebuildPlan.For(current, Array.Empty<RecordedProject>(), Schema, Vela);

        Assert.Equal(new[] { "data", "web" }, plan.Rebuild);
        Assert.True(plan.RebuildsEverything);
        Assert.NotEmpty(plan.Reasons);
    }

    [Fact]
    public void For_SelectsAProjectThatReferencesSomethingThisSolutionDoesNotHold()
    {
        // An edge pointing at nothing. It should not happen - Roslyn only reports
        // references it resolved - and this function is handed a graph out of a database,
        // so it cannot assume it. An upstream nobody can find is an upstream nobody can
        // prove is unchanged, and the rule for that is to rebuild.
        var current = new[]
        {
            Project("data", "d0"),
            Project("web", "w0", "data", "vanished")
        };

        var plan = RebuildPlan.For(current, Prior(("data", "d0"), ("web", "w0")), Schema, Vela);

        Assert.Equal(new[] { "web" }, plan.Rebuild);
        Assert.False(plan.RebuildsEverything);
        Assert.Contains(plan.Reasons, r => r.Contains("vanished", StringComparison.Ordinal));
    }

    [Fact]
    public void For_ReturnsTheSameSetInTheSameOrderWhateverOrderItIsGiven()
    {
        // Constraint 1. The plan is read by a human deciding whether to trust it and
        // written into a health record that a later run compares against, so the same
        // inputs have to produce the same sentence every time.
        var forwards = new[]
        {
            Project("base", "changed"),
            Project("left", "l0", "base"),
            Project("right", "r0", "base"),
            Project("shared", "s0", "left", "right")
        };

        var backwards = forwards.Reverse().ToArray();

        var prior = Prior(("base", "b0"), ("left", "l0"), ("right", "r0"), ("shared", "s0"));

        var one = RebuildPlan.For(forwards, prior, Schema, Vela);
        var two = RebuildPlan.For(backwards, prior.Reverse().ToArray(), Schema, Vela);

        Assert.Equal(one.Rebuild, two.Rebuild);
        Assert.Equal(one.Reuse, two.Reuse);
        Assert.Equal(one.Reasons, two.Reasons);
    }

    [Fact]
    public void For_SaysWhyEachProjectWasSelected()
    {
        // A plan nobody can check is a plan nobody should trust. Task 3 falls back to a
        // full rebuild whenever this cannot be relied on, and a fallback that says
        // nothing is the failure Constraint 3 exists to forbid.
        var current = new[]
        {
            Project("data", "changed"),
            Project("web", "w0", "data"),
            Project("tasks", "t0", "web")
        };

        var plan = RebuildPlan.For(
            current, Prior(("data", "d0"), ("web", "w0"), ("tasks", "t0")), Schema, Vela);

        Assert.Equal(new[] { "data", "tasks", "web" }, plan.Rebuild);

        Assert.Contains(plan.Reasons, r => r.StartsWith("data:", StringComparison.Ordinal)
                                           && r.Contains("inputs changed", StringComparison.Ordinal));
        Assert.Contains(plan.Reasons, r => r.StartsWith("web:", StringComparison.Ordinal)
                                           && r.Contains("data", StringComparison.Ordinal));
        Assert.Contains(plan.Reasons, r => r.StartsWith("tasks:", StringComparison.Ordinal)
                                           && r.Contains("web", StringComparison.Ordinal));
    }

    [Fact]
    public void For_SelectsNothingWhenTheSolutionHasNoProjectsAndTheLedgerAgrees()
    {
        var plan = RebuildPlan.For(
            Array.Empty<ProjectFingerprint>(), Array.Empty<RecordedProject>(), Schema, Vela);

        Assert.Empty(plan.Rebuild);
        Assert.Empty(plan.Reuse);
    }

    /// <summary>
    /// Two projects compiling one file is ordinary - a linked file, a shared source
    /// directory, a file included by a wildcard from two places. A document in the index
    /// holds the occurrences of EVERY project that compiles it, because documents are
    /// keyed by the file a developer can open, so replacing that document on behalf of one
    /// project deletes the other's rows and nothing puts them back.
    /// </summary>
    [Fact]
    public void For_SelectsAProjectThatCompilesAFileASelectedProjectAlsoCompiles()
    {
        var current = new[] { Project("alpha", "changed"), Project("beta", "b0"), Project("gamma", "g0") };

        var plan = RebuildPlan.For(
            current,
            Prior(("alpha", "a0"), ("beta", "b0"), ("gamma", "g0")),
            Schema, Vela,
            Documents(
                ("alpha", new[] { "Alpha/Own.cs", "Shared/Shared.cs" }),
                ("beta", new[] { "Beta/Own.cs", "Shared/Shared.cs" }),
                ("gamma", new[] { "Gamma/Own.cs" })));

        Assert.Equal(new[] { "alpha", "beta" }, plan.Rebuild);
        Assert.Equal(new[] { "gamma" }, plan.Reuse);
        Assert.Contains(plan.Reasons, r => r.StartsWith("beta:", StringComparison.Ordinal)
                                           && r.Contains("Shared/Shared.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void For_ClosesOverSharedDocumentsTransitively()
    {
        // alpha shares a file with beta, beta shares a different file with gamma. Rebuild
        // alpha and all three documents are being replaced, so all three projects have to
        // contribute to them.
        var current = new[] { Project("alpha", "changed"), Project("beta", "b0"), Project("gamma", "g0") };

        var plan = RebuildPlan.For(
            current,
            Prior(("alpha", "a0"), ("beta", "b0"), ("gamma", "g0")),
            Schema, Vela,
            Documents(
                ("alpha", new[] { "one.cs" }),
                ("beta", new[] { "one.cs", "two.cs" }),
                ("gamma", new[] { "two.cs" })));

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, plan.Rebuild);
        Assert.Empty(plan.Reuse);
    }

    [Fact]
    public void For_DoesNotSelectAProjectThatSharesNoDocument()
    {
        var current = new[] { Project("alpha", "changed"), Project("beta", "b0") };

        var plan = RebuildPlan.For(
            current,
            Prior(("alpha", "a0"), ("beta", "b0")),
            Schema, Vela,
            Documents(("alpha", new[] { "one.cs" }), ("beta", new[] { "two.cs" })));

        Assert.Equal(new[] { "alpha" }, plan.Rebuild);
        Assert.Equal(new[] { "beta" }, plan.Reuse);
    }

    [Fact]
    public void For_ReturnsTheSameSharedDocumentClosureWhateverOrderItIsGiven()
    {
        var forwards = new[] { Project("alpha", "changed"), Project("beta", "b0"), Project("gamma", "g0") };
        var backwards = forwards.Reverse().ToArray();

        var documents = Documents(
            ("alpha", new[] { "one.cs" }),
            ("beta", new[] { "one.cs", "two.cs" }),
            ("gamma", new[] { "two.cs" }));

        var prior = Prior(("alpha", "a0"), ("beta", "b0"), ("gamma", "g0"));

        var one = RebuildPlan.For(forwards, prior, Schema, Vela, documents);
        var other = RebuildPlan.For(backwards, prior, Schema, Vela, documents);

        Assert.Equal(one.Rebuild, other.Rebuild);
        Assert.Equal(one.Reuse, other.Reuse);
        Assert.Equal(one.Reasons, other.Reasons);
    }

    private static Dictionary<string, IReadOnlyList<string>> Documents(
        params (string Project, string[] Paths)[] entries) =>
        entries.ToDictionary(e => e.Project, e => (IReadOnlyList<string>)e.Paths, StringComparer.Ordinal);

    /// <summary>
    /// a is upstream of b is upstream of c is upstream of d: four projects, three deep,
    /// and every one of them a different distance from the change.
    /// </summary>
    private static ProjectFingerprint[] Chain() => new[]
    {
        Project("a", "a0"),
        Project("b", "b0", "a"),
        Project("c", "c0", "b"),
        Project("d", "d0", "c")
    };

    private static ProjectFingerprint Project(string identity, string fingerprint, params string[] references) =>
        new(identity, identity, fingerprint, Array.Empty<ProjectInput>(), references);

    private static RecordedProject[] Prior(params (string Project, string Fingerprint)[] projects) =>
        projects
            .Select(p => new RecordedProject(p.Project, p.Fingerprint, Schema, Vela, Array.Empty<string>()))
            .ToArray();
}
