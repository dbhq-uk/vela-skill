using System.CommandLine;
using Microsoft.Data.Sqlite;
using Vela.Config;
using Vela.Indexing;
using Vela.Query;

public static class Program
{
    /// <summary>
    /// Exit code for a question vela could not answer at all: no solution, or no
    /// index built yet. Distinct from <see cref="IndexHealth.ExitDegraded"/>, which
    /// means an answer was produced but the index behind it is known to be missing
    /// code, and distinct from 0, which promises neither problem.
    /// </summary>
    public const int ExitCannotAnswer = 1;

    /// <summary>
    /// Detail lines beyond this many are summarised rather than listed. The banner
    /// is printed above every answer from a degraded index, and a wall of text stops
    /// being read.
    ///
    /// One number, shared with <see cref="IndexHealth"/>, which builds the other half of
    /// the same banner. Two constants meant the same index rendered a bounded detail from
    /// one path and an unbounded one from the other.
    /// </summary>
    private const int MaxDetailProblems = IndexHealth.MaxDetailProblems;

    /// <summary>
    /// Every prefix ScipEmitter uses to record, into the emitted index, a reason that
    /// index is missing code. One list, so a new kind of problem reaches the health
    /// record by being recorded rather than by also being remembered here.
    ///
    /// Membership is the test of one question: does this mean code from the repository
    /// being indexed is absent? Not every note the emitter leaves does, and the ones
    /// that do not must stay out, or the banner fires when nothing is wrong and stops
    /// being read.
    /// </summary>
    private static readonly string[] ProblemPrefixes =
    {
        LoadFailurePrefix,
        OutsideProjectRootPrefix,
        CompileErrorPrefix,
        NoCompilationPrefix
    };

    private const string LoadFailurePrefix = "load-failure:";
    private const string OutsideProjectRootPrefix = "outside-project-root:";
    private const string CompileErrorPrefix = "compile-error:";
    private const string NoCompilationPrefix = "no-compilation:";

    /// <summary>
    /// Deliberately absent from <see cref="ProblemPrefixes"/>. A document outside the
    /// repository is a file this index was never going to hold, so it is reported and
    /// counted but never treated as a gap in the code being indexed.
    /// </summary>
    private const string ExternalDocumentPrefix = "external-document:";

    private const string NoSolutionMessage =
        "No single .sln found in the current directory. Pass --solution <path to the .sln>.";

    public static Task<int> Main(string[] args) =>
        BuildRootCommand().Parse(args).InvokeAsync();

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Compiler-exact code search for .NET.");

        var solutionOption = new Option<string>("--solution")
        {
            Description = "Path to the .sln. Defaults to the only .sln in the current directory.",
            DefaultValueFactory = _ => FindSolution()
        };

        const string symbolHelp =
            "Symbol name, matched a whole dotted segment at a time and case-sensitively, "
            + "for example Status or Perfume.Status.";

        root.Add(BuildIndexCommand(solutionOption));
        root.Add(BuildImportCommand(solutionOption));
        root.Add(BuildFindCommand(solutionOption));
        root.Add(BuildHitCommand("def", "Where a symbol is defined",
            "symbol", symbolHelp,
            solutionOption, (db, value, _) => DefQuery.Run(db, value), DefQuery.ExplainEmpty,
            hitsAreOccurrencesOfTheArgument: true));
        root.Add(BuildHitCommand("refs", "Every usage of a symbol",
            "symbol", symbolHelp,
            solutionOption, RefsQuery.Run, RefsQuery.ExplainEmpty, RefsQuery.CountInGeneratedCode,
            hitsAreOccurrencesOfTheArgument: true));
        root.Add(BuildHitCommand("outline", "Symbols defined in a file",
            "file", "Path of the file, relative to the repository root (the solution directory "
                  + "when the solution is not in a repository).",
            solutionOption, (db, value, _) => OutlineQuery.Run(db, value), OutlineQuery.ExplainEmpty));
        root.Add(BuildHitCommand("impact", "Callers and blast radius",
            "symbol", symbolHelp,
            solutionOption, ImpactQuery.Run, ImpactQuery.ExplainEmpty, ImpactQuery.CountInGeneratedCode,
            matchedSymbols: ImpactQuery.MatchedSymbols));

        return root;
    }

    /// <summary>
    /// One of the four verbs that answer with a list of hits. They differ only in
    /// which query they run, in what their single argument means, and in what an
    /// empty answer from them can honestly be said to mean.
    /// </summary>
    /// <param name="countInGeneratedCode">
    /// Supplied by the verbs that suppress generated documents by default (refs and
    /// impact), and null for the verbs that always report them (def and outline).
    /// Supplying it is what adds --include-generated and what makes the verb declare
    /// the size of what it left out, so a verb cannot start suppressing results without
    /// also gaining the sentence that says it did (Constraint 3).
    /// </param>
    /// <param name="hitsAreOccurrencesOfTheArgument">
    /// True for refs and def, whose rows are occurrences of the symbol asked about, so
    /// the ambiguity block can be tallied straight off the answer and its counts add up
    /// to the reported total. False for outline, whose argument is a file path: every
    /// file defines several symbols, so a notice there would fire on every outline ever
    /// run, which is the loudest possible way of crying wolf. False for impact too,
    /// which supplies <paramref name="matchedSymbols"/> instead.
    /// </param>
    /// <param name="matchedSymbols">
    /// Supplied by impact alone. Its rows name the CALLERS, so the symbols the pattern
    /// matched appear nowhere in its answer and the tally has to come from the index.
    /// That is also why it is asked even when impact named nobody: an empty answer to an
    /// ambiguous pattern is explained in the singular, and the block is the only thing
    /// that says the explanation covers several symbols at once.
    /// </param>
    private static Command BuildHitCommand(
        string name, string description,
        string argumentName, string argumentDescription,
        Option<string> solutionOption,
        Func<SqliteConnection, string, bool, IReadOnlyList<Hit>> run,
        Func<SqliteConnection, string, string> explainEmpty,
        Func<SqliteConnection, string, int>? countInGeneratedCode = null,
        bool hitsAreOccurrencesOfTheArgument = false,
        Func<SqliteConnection, string, bool, IReadOnlyList<SymbolTally>>? matchedSymbols = null)
    {
        var argument = new Argument<string>(argumentName) { Description = argumentDescription };
        var command = new Command(name, description) { argument, solutionOption };

        Option<bool>? includeGeneratedOption = null;
        if (countInGeneratedCode is not null)
        {
            includeGeneratedOption = new Option<bool>("--include-generated")
            {
                Description = "Also report occurrences in source-generated code, which is compiled "
                            + "but not written to disk and so cannot be opened."
            };
            command.Add(includeGeneratedOption);
        }

        command.SetAction(parseResult =>
        {
            var output = parseResult.InvocationConfiguration.Output;
            var error = parseResult.InvocationConfiguration.Error;

            var solution = parseResult.GetValue(solutionOption);
            using var db = OpenIndex(solution, error);
            if (db is null) return ExitCannotAnswer;

            // Deliberately not wrapped in a catch: IndexHealth.Read throws when the
            // health table is missing or its timestamp is unreadable, and both mean
            // the index cannot be vouched for. Swallowing that would report a clean
            // answer from an index nobody has checked.
            var health = CheckStaleness(IndexHealth.Read(db), solution!);
            var value = parseResult.GetRequiredValue(argument);

            // def and outline have no option and always include generated documents;
            // refs and impact exclude them unless asked.
            var includeGenerated = includeGeneratedOption is null
                                   || parseResult.GetValue(includeGeneratedOption);

            var hits = run(db, value, includeGenerated);

            // The reason is worked out only when there is nothing to report, so the
            // normal answer costs no extra query.
            var explanation = hits.Count == 0 ? explainEmpty(db, value) : null;

            output.Write(OutputWriter.Render(hits, health, explanation));

            // Printed here rather than after the ambiguity block, because it qualifies
            // the result count and a screen of symbol names between the two leaves the
            // number reading as the whole answer.
            if (countInGeneratedCode is not null && !includeGenerated)
            {
                var suppressed = countInGeneratedCode(db, value);
                if (suppressed > 0)
                {
                    output.WriteLine($"{suppressed} further result(s) in generated code, which is not on "
                                   + "disk. Pass --include-generated to see them.");
                }
            }

            // Last, because it is the longest thing in the answer and it qualifies
            // everything above it. refs and def tally their own rows; impact cannot,
            // so it reads the symbols the pattern matched from the index and says so in
            // different words. Neither prints anything when one symbol was matched.
            if (hitsAreOccurrencesOfTheArgument)
                output.Write(Ambiguity.RenderOccurrences(value, Ambiguity.Of(hits)));
            else if (matchedSymbols is not null)
                output.Write(Ambiguity.RenderCallers(value, matchedSymbols(db, value, includeGenerated), hits.Count > 0));

            return health.Degraded ? IndexHealth.ExitDegraded : 0;
        });

        return command;
    }

    private static Command BuildFindCommand(Option<string> solutionOption)
    {
        var argument = new Argument<string>("pattern") { Description = "Text to look for in symbol names." };
        var command = new Command("find", "Symbol search by name") { argument, solutionOption };

        command.SetAction(parseResult =>
        {
            var output = parseResult.InvocationConfiguration.Output;
            var error = parseResult.InvocationConfiguration.Error;

            var solution = parseResult.GetValue(solutionOption);
            using var db = OpenIndex(solution, error);
            if (db is null) return ExitCannotAnswer;

            var health = CheckStaleness(IndexHealth.Read(db), solution!);
            var symbols = FindQuery.Run(db, parseResult.GetRequiredValue(argument));

            // find answers with names rather than hits, but a degraded index makes
            // its list just as incomplete, so it carries the same banner and the
            // same exit code.
            output.Write(OutputWriter.RenderBanner(health));
            foreach (var symbol in symbols) output.WriteLine(symbol);
            output.WriteLine();
            output.WriteLine($"{symbols.Count} symbol(s)");

            // "0 symbol(s)" on its own reads as an authoritative "no such name exists".
            // find is the discovery verb, so that is the sentence an agent acts on
            // before deciding something is not in the codebase (Constraint 3).
            if (symbols.Count == 0)
                output.WriteLine(FindQuery.ExplainEmpty(db, parseResult.GetRequiredValue(argument)));

            return health.Degraded ? IndexHealth.ExitDegraded : 0;
        });

        return command;
    }

    private static Command BuildIndexCommand(Option<string> solutionOption)
    {
        // The coverage this option reports is the one property of vela that regresses
        // silently: lose the source-generated documents and the index still builds,
        // every query still answers, and Razor and Blazor are simply not in it. A count
        // is the only thing that shows it, so validating a change means running this.
        var statsOption = new Option<bool>("--stats")
        {
            Description = "After indexing, print document, generated-document, Razor, occurrence "
                        + "and definition counts."
        };

        // Off by default, and it will stay off until this version has been proven in use.
        // A full rebuild cannot be stale, because it reads everything. An incremental one
        // is a CLAIM that what it skipped has not changed, and if the claim is wrong the
        // index holds rows describing code that no longer exists while reporting itself
        // complete - Constraint 3's exact failure, and worse than the slowness it replaces.
        var incrementalOption = new Option<bool>("--incremental")
        {
            Description = "Rebuild only the projects whose inputs changed, and every project downstream "
                        + "of them. Off by default. Falls back to a full rebuild, saying so, whenever the "
                        + "decision cannot be trusted. It helps most when the change is in a leaf project; "
                        + "a change low in the dependency graph rebuilds nearly everything."
        };

        var command = new Command("index", "Build the index for a solution")
        {
            solutionOption, statsOption, incrementalOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var output = parseResult.InvocationConfiguration.Output;
            var error = parseResult.InvocationConfiguration.Error;

            // Read before anything expensive happens. A config vela cannot honour must cost
            // nothing and change nothing, and the config is allowed to say which solution
            // this is, so both questions are settled before the workspace is loaded.
            if (!TryResolveSolution(parseResult.GetValue(solutionOption), error, out var solution, out var config))
                return ExitCannotAnswer;

            var repositoryRoot = ProjectRoot.ForSolution(solution);
            var plan = JobPlanner.For(config, repositoryRoot);

            // Every line about the config is inside this guard, and deliberately so: with
            // no vela.json the output of this verb is what it always was, to the character.
            if (config.FoundAt is not null)
            {
                output.WriteLine($"Using {VelaConfig.FileName} at {config.FoundAt}: {DescribeJobs(config)}");
                foreach (var note in plan.Notes) output.WriteLine(note);

                if (!plan.RunsVelaIndexer)
                {
                    error.WriteLine($"{VelaConfig.FileName} declares no job for vela's own indexer, so there is "
                                  + "nothing for vela index to build. Add a csharp job, or leave the jobs array "
                                  + "out to keep the default csharp and razor jobs. Another indexer's output "
                                  + "reaches this index through vela import.");
                    return ExitCannotAnswer;
                }
            }

            var path = IndexPaths.ForSolution(solution);

            // ForSolution is pure, so the cache directory may not exist yet, and
            // ScipLoader.Load requires an empty schema: re-indexing into the last
            // build's database is a precondition violation, not an update.
            IndexPaths.EnsureDirectoryExists(path);

            var load = await Vela.Harvest.WorkspaceLoader.LoadAsync(solution, cancellationToken);

            // The incremental decision, taken before anything is harvested. Null means a
            // full rebuild, which is what this verb has always done and what it still does
            // unless asked otherwise - and every reason the decision was refused is printed
            // by the time this returns, because a fallback is a good outcome and a silent
            // one is not.
            var rebuild = parseResult.GetValue(incrementalOption)
                ? await PlanRebuildAsync(load.Solution, repositoryRoot, path, output, cancellationToken)
                : null;

            var emitted = await Vela.Harvest.ScipEmitter.EmitAsync(
                load.Solution, load.Failures, cancellationToken,
                rebuild is null ? null : rebuild.Reuse.ToHashSet(StringComparer.Ordinal));
            var index = emitted.Index;

            var replayed = new HashSet<string>(StringComparer.Ordinal);
            var replayProblems = new List<string>();
            HealthRecord health;

            if (rebuild is null)
            {
                health = BuildHealthRecord(index, load.Failures);

                // A job root that is not there can never be settled by an import, so it
                // belongs in the indexing pass's own verdict, which this run rewrites: fix
                // the config or the tree, index again, and it clears.
                if (plan.Problems.Count > 0)
                {
                    health = health with
                    {
                        Degraded = true,
                        Detail = Append(health.Detail, Summarise(plan.Problems))
                    };
                }

                // Asked BEFORE the delete, because the answer is in the file that is about
                // to go. This is what an imported language survives a rebuild by: nothing
                // else in the database outlives this line. See ImportedSources for what its
                // absence cost on the real repository.
                var remembered = ImportedSources.ReadFrom(path);

                if (File.Exists(path)) File.Delete(path);

                using var db = new SqliteConnection(ConnectionStringFor(path));
                db.Open();
                Schema.Create(db);
                ScipLoader.Load(db, emitted);

                var external = ExternalDocumentPaths(index);
                ExternalDocuments.Write(db, external);
                IndexHealth.Write(db, health);

                // What each project was built from, what each project could not do, and
                // which documents each one contributed to. Together they are what lets a
                // LATER run skip a project without forgetting any of it: a rebuild that
                // cannot check its own claim is the thing these records exist to make
                // impossible.
                ProjectInputs.Write(
                    db, emitted.Fingerprints, Schema.Version, ProjectInputs.VelaVersion, health.BuiltAtUtc);
                ProjectNotes.Write(db, emitted.Harvested, emitted.Notes);
                ProjectDocuments.Write(db, emitted.Harvested, emitted.DocumentsByProject);

                output.WriteLine($"Indexed {index.Documents.Count} documents to {path}");

                Replay(db, remembered, repositoryRoot, path, output, replayed, replayProblems);

                // Each job vela cannot run itself, recorded under the .scip it is waiting
                // for. That key is exactly the one `vela import` clears when it imports a
                // file cleanly, so importing the file the job named settles the job with no
                // further machinery and nothing left behind to go stale.
                //
                // A job whose .scip was just replayed is skipped: the replay has already
                // written that source's verdict under the same key, and it is the true one.
                // Writing "nothing has been imported from it" over the top would be the
                // crying-wolf failure with the wolf standing in the room.
                foreach (var pending in plan.Pending)
                {
                    if (!replayed.Contains(pending.Source))
                        IndexHealth.WriteImport(db, pending.Source, pending.Detail);
                }

                ReportExternalDocuments(output, external.Count);

                if (parseResult.GetValue(statsOption))
                    output.Write(IndexStatistics.Render(IndexStatistics.Read(db)));
            }
            else
            {
                using var db = new SqliteConnection(ConnectionStringFor(path));
                db.Open();

                // What the rebuilt projects contributed to LAST time, which is the only
                // record of a document whose file has since been deleted: the fresh harvest
                // cannot name a file that is not there.
                var replacing = DocumentsOf(ProjectDocuments.Read(db), rebuild.Rebuild);
                var written = ScipLoader.LoadIncremental(db, emitted, replacing);

                var builtAtUtc = DateTime.UtcNow;
                ProjectInputs.Write(
                    db, emitted.Fingerprints, Schema.Version, ProjectInputs.VelaVersion, builtAtUtc);
                ProjectNotes.Write(db, emitted.Harvested, emitted.Notes);
                ProjectDocuments.Write(db, emitted.Harvested, emitted.DocumentsByProject);

                // Everything every project has to say about itself, INCLUDING the projects
                // this run did not look at. That is what stops a skipped project going
                // quiet: a `compile-error:` note is produced by the harvest and by nothing
                // else, so a rebuild that only knew what it had just harvested would drop
                // the note and the index would stop calling itself degraded while still
                // holding an incomplete picture of that project.
                //
                // Deduplicated because the note for a path that cannot be a document is
                // attributed to whichever project reached it first, and that can be a
                // different project on a different run.
                var notes = ProjectNotes.Read(db)
                    .Select(note => note.Note)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var external = ExternalPathsIn(notes);
                ExternalDocuments.Replace(db, external);

                health = BuildHealthRecordFromNotes(notes, load.Failures, builtAtUtc);

                if (plan.Problems.Count > 0)
                {
                    health = health with
                    {
                        Degraded = true,
                        Detail = Append(health.Detail, Summarise(plan.Problems))
                    };
                }

                // A document the harvest produced and could not write, because an import
                // holds that path and relative_path is UNIQUE. Nothing was lost that was
                // not already somebody else's, and the index is still missing code it
                // harvested, so it says so.
                if (written.SkippedPaths.Count > 0)
                {
                    health = health with
                    {
                        Degraded = true,
                        Detail = Append(health.Detail, Summarise(written.SkippedPaths
                            .Select(p => "import-collision: " + p + " was harvested and not written, because "
                                       + "an imported .scip already holds that path. Run vela index without "
                                       + "--incremental, which rebuilds from nothing and replays the import "
                                       + "over the top.")
                            .ToList()))
                    };
                }

                health = health with { Rebuild = DescribeRebuild(rebuild) };
                IndexHealth.Write(db, health);

                ReportRebuild(output, rebuild, written, path);

                // An import survives an incremental rebuild by not being touched, where it
                // survives a full one by being replayed. Either way it is still in the
                // index and still remembered, so a job waiting on it is still settled.
                replayed.UnionWith(ImportedPaths(db));

                foreach (var pending in plan.Pending)
                {
                    if (!replayed.Contains(pending.Source))
                        IndexHealth.WriteImport(db, pending.Source, pending.Detail);
                }

                ReportExternalDocuments(output, external.Count);

                if (parseResult.GetValue(statsOption))
                    output.Write(IndexStatistics.Render(IndexStatistics.Read(db)));
            }

            var outstanding = plan.Pending.Where(p => !replayed.Contains(p.Source)).ToList();

            if (config.FoundAt is not null)
            {
                ReportCensus(output, config, repositoryRoot);
                ReportPendingJobs(output, outstanding, repositoryRoot);
            }

            var reasons = outstanding
                .Select(p => Relative(repositoryRoot, p.Source) + ": " + p.Detail)
                .Concat(replayProblems)
                .ToList();

            var detail = reasons.Count == 0 ? health.Detail : Append(health.Detail, Summarise(reasons));

            // A configured job that did not run degrades the index exactly as a project that
            // did not load does. That is the whole point of putting the jobs in a file: a
            // config must never become a quiet way to lose a language. A .scip that was
            // imported once and cannot be replayed is the same fact arriving from the other
            // direction, and it degrades the index for the same reason.
            if (health.Degraded || reasons.Count > 0)
            {
                error.WriteLine("!! The index is INCOMPLETE. " + detail);
                error.WriteLine("   Answers from it may be missing code. Do not treat an empty result as proof.");
                return IndexHealth.ExitDegraded;
            }

            return 0;
        });

        return command;
    }

    /// <summary>
    /// Which projects this run can honestly skip, or null when it can skip none of them.
    ///
    /// <b>Every null is printed before it is returned.</b> A fallback to a full rebuild is
    /// a good outcome - it is the only outcome that cannot be stale - and it must never be
    /// silent, because a user who asked for a fast rebuild and got a slow one needs to know
    /// which one they got and why. There are five ways to reach it: no index yet, a schema
    /// this build does not read, a whole-index reason in the plan, a plan that reaches
    /// every project anyway, and anything at all going wrong while working it out. The last
    /// of those means anything that is not the user cancelling; see the catch below.
    ///
    /// The fingerprinting here does not compile anything, which is what makes the decision
    /// affordable: on the real solution it is 554ms cold and 76ms warm against a full index
    /// of 2m13s, and the plan itself is 7 to 9ms over ten projects and thirty-four edges.
    /// </summary>
    private static async Task<RebuildPlan?> PlanRebuildAsync(
        Microsoft.CodeAnalysis.Solution solution,
        string repositoryRoot,
        string indexPath,
        TextWriter output,
        CancellationToken ct)
    {
        if (!File.Exists(indexPath))
        {
            FallBackToFullRebuild(output,
                $"there is no index at {indexPath} yet, so there is nothing to compare this tree against.");
            return null;
        }

        try
        {
            RebuildPlan plan;

            using (var db = new SqliteConnection(ConnectionStringFor(indexPath)))
            {
                db.Open();

                var version = Schema.ReadVersion(db);
                if (version != Schema.Version)
                {
                    FallBackToFullRebuild(output,
                        $"the index at {indexPath} was built against schema version {version} and this vela "
                        + $"reads schema version {Schema.Version}, so nothing recorded in it means what this "
                        + "build would write.");
                    return null;
                }

                plan = RebuildPlan.For(
                    await ProjectFingerprint.ForSolutionAsync(
                        solution.Projects.ToArray(), repositoryRoot, ct),
                    ProjectInputs.Read(db),
                    Schema.Version,
                    ProjectInputs.VelaVersion,
                    ProjectDocuments.Read(db));
            }

            if (plan.RebuildsEverything)
            {
                FallBackToFullRebuild(output, string.Join(" ", plan.Reasons));
                return null;
            }

            // NOT the same fact as the flag above, and worth keeping straight. The flag
            // means a whole-index reason fired: something made every recorded row
            // untrustworthy. This means the closure was right and happened to reach every
            // project, which is what a change low in the dependency graph looks like -
            // ordinary, correct, and expensive. Rebuilding them one at a time would produce
            // the same index by a slower route, having paid for the decision first.
            if (plan.Reuse.Count == 0)
            {
                FallBackToFullRebuild(output,
                    $"the change reaches every project in this solution, all {plan.Rebuild.Count} of them, so "
                    + "there is nothing left to reuse. "
                    + (plan.Reasons.Count > 0 ? plan.Reasons[0] + "." : ""));
                return null;
            }

            return plan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything at all, and it says anything at all because it means it. This used
            // to name five exception types, which is a list of what somebody thought of
            // rather than a principle: a ledger holding two rows for one project threw an
            // ArgumentException that was not on it, and the whole command died rather than
            // building the index it was asked for.
            //
            // The plan is the part of this feature that can be wrong silently, so a plan
            // that threw is a plan nobody should act on, and the only safe reading of one
            // is that there is no plan. There is no failure for which refusing to build an
            // index is better than building it the slow way, and nothing is hidden by
            // choosing the slow way: the exception's own message is printed on the line
            // above, so a bug in here is louder than it was, not quieter.
            //
            // Cancellation is the one thing not swallowed. A user who pressed Ctrl-C asked
            // for less work, and answering that with a full rebuild would be the opposite
            // of what they asked for. It leaves by the same route it always did.
            FallBackToFullRebuild(output, "the plan could not be worked out: " + ex.Message);
            return null;
        }
    }

    private static void FallBackToFullRebuild(TextWriter output, string reason)
    {
        output.WriteLine("Falling back to a full rebuild: " + reason);
        output.WriteLine("   A full rebuild cannot be stale, because it reads everything. This is the safe "
                       + "outcome and not a failure.");
    }

    /// <summary>
    /// What the incremental rebuild did, project by project, because the one thing a
    /// reader needs from this verb is which parts of the answer were looked at and which
    /// were taken on trust.
    /// </summary>
    private static void ReportRebuild(
        TextWriter output, RebuildPlan plan, ScipLoader.IncrementalLoad written, string path)
    {
        var total = plan.Rebuild.Count + plan.Reuse.Count;

        output.WriteLine($"Incremental rebuild: {plan.Rebuild.Count} of {total} project(s) rebuilt, "
                       + $"{plan.Reuse.Count} reused.");

        foreach (var reason in plan.Reasons.Take(MaxDetailProblems))
            output.WriteLine("  rebuilt " + reason);

        if (plan.Reasons.Count > MaxDetailProblems)
            output.WriteLine($"  (+{plan.Reasons.Count - MaxDetailProblems} more rebuilt)");

        foreach (var reused in plan.Reuse.Take(MaxDetailProblems))
            output.WriteLine("  reused " + reused);

        if (plan.Reuse.Count > MaxDetailProblems)
            output.WriteLine($"  (+{plan.Reuse.Count - MaxDetailProblems} more reused)");

        output.WriteLine($"Replaced {written.DocumentsRemoved} document(s) with {written.DocumentsWritten} "
                       + $"in {path}. Every other document in it is the one the last build wrote, and this "
                       + "run did not look at the code behind it.");
    }

    /// <summary>
    /// The sentence the index keeps about how it was last built. Null for a full rebuild,
    /// which is what makes "this index was built whole" the thing an absent value means.
    /// </summary>
    private static string DescribeRebuild(RebuildPlan plan)
    {
        var total = plan.Rebuild.Count + plan.Reuse.Count;

        return $"incremental: {plan.Rebuild.Count} of {total} project(s) rebuilt ({Names(plan.Rebuild)}); "
             + $"{plan.Reuse.Count} reused ({Names(plan.Reuse)})";
    }

    private static string Names(IReadOnlyList<string> projects)
    {
        if (projects.Count == 0) return "none";

        var shown = string.Join(", ", projects.Take(MaxDetailProblems));
        return projects.Count > MaxDetailProblems
            ? $"{shown}, (+{projects.Count - MaxDetailProblems} more)"
            : shown;
    }

    /// <summary>
    /// Every document the projects being rebuilt contributed to when the index was last
    /// built, deduplicated because two of them can share one.
    /// </summary>
    private static List<string> DocumentsOf(
        IReadOnlyDictionary<string, IReadOnlyList<string>> recorded, IReadOnlyList<string> projects)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            if (recorded.TryGetValue(project, out var documents)) paths.UnionWith(documents);
        }

        return paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    /// <summary>The .scip files this index already holds documents from.</summary>
    private static IReadOnlyList<string> ImportedPaths(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT source FROM imported_source ORDER BY source";
        using var reader = cmd.ExecuteReader();

        var sources = new List<string>();
        while (reader.Read()) sources.Add(reader.GetString(0));
        return sources;
    }

    /// <summary>
    /// The files a NuGet package or the .NET SDK contributed, recovered from the notes.
    /// An incremental rebuild sees only the projects it looked at, so the whole list has
    /// to be assembled from what every project recorded rather than from this run alone.
    /// </summary>
    private static IReadOnlyList<string> ExternalPathsIn(IReadOnlyList<string> notes) =>
        notes
            .Where(note => note.StartsWith(ExternalDocumentPrefix, StringComparison.Ordinal))
            .Select(note => note[ExternalDocumentPrefix.Length..].Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Said plainly, and once. These files are not in the index and the number is worth
    /// knowing, but they were never this index's to hold, so saying it through the "!!"
    /// banner would raise the exit code and teach the reader to ignore the banner
    /// (Constraint 3 cuts both ways).
    ///
    /// The sentence claims only what was checked. A document reaches this count by living
    /// under the NuGet package cache or under the .NET installation, and nothing else does:
    /// a file merely outside the repository is first-party code until shown otherwise and
    /// goes to the banner. So naming those two places is exact, where "from outside this
    /// repository" was a wider claim than the test behind it.
    /// </summary>
    private static void ReportExternalDocuments(TextWriter output, int count)
    {
        if (count == 0) return;

        output.WriteLine($"{count} document(s) contributed by a NuGet package or "
                       + "the .NET SDK were not indexed. They live in the package cache or "
                       + "the .NET installation, not in this repository, so none of your "
                       + "code is missing because of them. Run vela index --stats to list "
                       + "them.");
    }

    /// <summary>
    /// Which solution this command is about, and what `vela.json` says about the repository
    /// it sits in.
    ///
    /// The config is looked for from the solution's own directory upwards, bounded by the
    /// repository root, and it may supply the solution itself: a repository with two .sln
    /// files could otherwise only be indexed by passing --solution on every command, which
    /// is exactly the kind of thing a config file exists to settle.
    ///
    /// A config that cannot be honoured stops the command here, before anything is read or
    /// written. Half-honouring it would mean building an index that means something other
    /// than what was asked for, and saying nothing about the difference.
    /// </summary>
    private static bool TryResolveSolution(
        string? solution, TextWriter error, out string resolvedSolution, out VelaConfig config)
    {
        resolvedSolution = "";
        config = VelaConfig.Default;

        var start = string.IsNullOrWhiteSpace(solution)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(solution)) ?? Directory.GetCurrentDirectory();

        try
        {
            config = VelaConfig.ForDirectory(start);
        }
        catch (VelaConfigException ex)
        {
            foreach (var problem in ex.Problems) error.WriteLine(problem);
            error.WriteLine("Nothing was read or written, so any index already built is exactly as it was.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(solution))
        {
            resolvedSolution = solution;
            return true;
        }

        if (string.IsNullOrWhiteSpace(config.Solution))
        {
            error.WriteLine(NoSolutionMessage);
            return false;
        }

        if (!File.Exists(config.Solution))
        {
            error.WriteLine($"{VelaConfig.FileName} at {config.FoundAt} names a solution that is not there: "
                          + config.Solution);
            return false;
        }

        resolvedSolution = config.Solution;
        return true;
    }

    /// <summary>What the configured jobs are, in one line, said the way the file says it:
    /// a language, and who produces it.</summary>
    private static string DescribeJobs(VelaConfig config)
    {
        var vela = config.Jobs.Where(j => j.IsVela).Select(j => j.Language).ToList();

        var parts = new List<string>();
        if (vela.Count > 0) parts.Add(string.Join(" and ", vela) + " from vela's own indexer");
        parts.AddRange(config.Jobs.Where(j => !j.IsVela)
            .Select(j => $"{j.Language} from {j.Indexer} at '{j.Root}'"));

        return $"{config.Jobs.Count} job(s): {string.Join("; ", parts)}.";
    }

    /// <summary>
    /// What the repository is written in that this index does not hold.
    ///
    /// It is reported rather than warned about, in the same voice as the documents a NuGet
    /// package contributed, and it never raises the exit code: vela cannot index SQL and
    /// never claimed to, so calling that an incomplete index would teach the reader to
    /// ignore the banner. What it is for is the measurement that justified the whole file -
    /// seeing "python 80" where a naive count says 5,775 is how anybody checks that the
    /// exclude list is doing its job.
    ///
    /// Only run when a vela.json exists, so a repository that has never heard of the file
    /// pays nothing for this walk.
    /// </summary>
    private static void ReportCensus(TextWriter output, VelaConfig config, string repositoryRoot)
    {
        var census = LanguageCensus.Take(repositoryRoot, PathFilter.For(config.Exclude));
        var uncovered = census.NotCoveredBy(config.Jobs);

        if (uncovered.Count > 0)
        {
            var named = string.Join(", ", uncovered
                .Take(MaxDetailProblems)
                .Select(l => $"{l.Language} {l.Files} file(s)"));

            if (uncovered.Count > MaxDetailProblems)
                named += $", (+{uncovered.Count - MaxDetailProblems} more)";

            output.WriteLine($"No job covers {named}, so none of it is in this index. Nothing of yours is "
                           + "missing that a job asked for; this is what the repository holds beside it.");
        }

        output.WriteLine($"The exclude list kept this count out of {census.PrunedDirectories} "
                       + $"director(ies) and rejected {census.ExcludedFiles} further file(s).");
    }

    /// <summary>
    /// The jobs this index is waiting on, and the command that would settle each one.
    ///
    /// vela does not run other indexers: `scip-io` exists for orchestration and vela consumes
    /// merged output, so a job whose indexer is not vela declares where its .scip is expected
    /// to come from. That is what makes it reportable rather than ignorable, which is the
    /// point - the alternative is a config file that can quietly lose a language.
    /// </summary>
    private static void ReportPendingJobs(
        TextWriter output, IReadOnlyList<PendingJob> pending, string repositoryRoot)
    {
        if (pending.Count == 0) return;

        output.WriteLine($"{pending.Count} configured job(s) are not in this index. vela does not run "
                       + "other indexers, so each one's .scip has to be produced and imported; until it is, "
                       + "this index is missing that language and says so on every answer:");

        foreach (var job in pending)
            output.WriteLine($"  {Relative(repositoryRoot, job.Source)}: {job.Detail}");
    }

    /// <summary>
    /// A .scip that was imported into the index this run is replacing, and is not there
    /// any more. The prefix is a kind, like the emitter's own, so the reason is
    /// classifiable rather than a sentence that has to be read to be sorted.
    /// </summary>
    private const string ImportLostPrefix = "import-lost: ";

    /// <summary>A .scip that is still there and could not be read. Distinct from
    /// <see cref="ImportLostPrefix"/> because the two want different things done about
    /// them: one file has to be produced again, the other has to be looked at.</summary>
    private const string ImportUnreadablePrefix = "import-unreadable: ";

    /// <summary>
    /// Puts back every .scip that had been imported into the index this run has just
    /// destroyed and rebuilt.
    ///
    /// <b>Why the rebuild replays rather than merely listing what to re-import.</b> Both
    /// were open. A list leaves the index genuinely missing the language until somebody
    /// acts on it, which means `vela index` stops being a command that produces a usable
    /// index, and it puts the burden on the person least likely to be watching - the one
    /// who ran a routine re-index. Replaying restores what the user already asked for from
    /// files they already produced, using the same importer and the same code path a
    /// manual re-import would. Nothing is assumed away by doing it: the weaker behaviour is
    /// still exactly what happens for a file that cannot be replayed, which is the only
    /// place it is honest.
    ///
    /// <b>What it refuses to pretend.</b> A .scip on disk now need not be the one that was
    /// imported - an indexer is normally re-run over changed code between one index and the
    /// next - so the content hash is compared and a file that has changed is re-imported
    /// and SAID to have changed. A file that has gone, or that will not read, degrades the
    /// index and names itself, and that verdict is written under the same key `vela import`
    /// clears, so producing the file and importing it settles it.
    /// </summary>
    private static void Replay(
        SqliteConnection db,
        RememberedImports remembered,
        string repositoryRoot,
        string indexPath,
        TextWriter output,
        HashSet<string> replayed,
        List<string> problems)
    {
        if (remembered.RecordUnavailable)
        {
            output.WriteLine("The index this replaced was built before vela recorded which .scip files had "
                           + "been imported into it, so there was nothing to replay. If a language was "
                           + "imported into that index, import it again.");
            return;
        }

        if (remembered.Sources.Count == 0) return;

        var lines = new List<string>();

        foreach (var record in remembered.Sources)
        {
            replayed.Add(record.Source);

            if (!File.Exists(record.Source))
            {
                Lost(record, ImportLostPrefix + record.Source + ": it was imported into the index this run "
                    + "replaced and is not there now, so the code it carried is not in this index. Produce "
                    + "it again and run: vela import " + record.Source + ". If that language is no longer "
                    + "wanted, delete " + indexPath + " and run vela index again, which starts from nothing.");
                continue;
            }

            string hash;
            ImportReport report;
            try
            {
                hash = ImportedSources.HashOf(record.Source);
                report = ScipImporter.ImportFile(
                    db, record.Source, repositoryRoot, replace: false, source: record.Source);
            }
            catch (Exception ex)
                when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Lost(record, ImportUnreadablePrefix + record.Source + ": " + ex.Message
                    + " It was imported into the index this run replaced, so the code it carried is not in "
                    + "this index. Fix it and run: vela import " + record.Source);
                continue;
            }

            ImportedSources.Write(db, new ImportedSource(
                record.Source, DateTime.UtcNow, hash, report.Documents, report.Occurrences));

            var detail = report.Degraded ? Summarise(report.Problems) : null;
            IndexHealth.WriteImport(db, record.Source, detail);
            if (detail is not null) problems.Add(Relative(repositoryRoot, record.Source) + ": " + detail);

            var changed = !string.Equals(hash, record.ContentHash, StringComparison.Ordinal);
            lines.Add($"  {Relative(repositoryRoot, record.Source)}: {report.Documents} document(s) and "
                    + $"{report.Occurrences} occurrence(s)."
                    + (changed
                        ? " The file has CHANGED since it was imported, so this index holds what is in it "
                          + $"now rather than the {record.Documents} document(s) it held before."
                        : ""));
        }

        output.WriteLine($"Replayed {remembered.Sources.Count} imported .scip file(s) that the index this "
                       + "run replaced had been built from. vela index rebuilds from nothing, so without "
                       + "this every imported language would leave the index without a word being said:");
        foreach (var line in lines) output.WriteLine(line);

        void Lost(ImportedSource record, string problem)
        {
            problems.Add(problem);
            IndexHealth.WriteImport(db, record.Source, problem);

            // Kept, not forgotten. The file is still what this index was built from, so a
            // later rebuild has to go on saying it is missing until somebody either
            // imports it or throws the index away. Forgetting it here would make the
            // SECOND rebuild silent, which is the failure this whole mechanism exists for.
            ImportedSources.Write(db, record);
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Append(string? existing, string detail) =>
        string.IsNullOrEmpty(existing) ? detail : existing + "; " + detail;

    /// <summary>
    /// `vela import <file.scip>`: read an index another language's indexer produced into
    /// the same database vela queries, so refs, def, outline and impact answer across
    /// languages.
    ///
    /// It ADDS. `vela index` deletes the database and rebuilds it, because
    /// ScipLoader.Load is a one-shot bulk load; import is the opposite by construction,
    /// since the whole point is a second language beside the first. So the order is
    /// `vela index` and then `vela import`, and re-running `vela index` throws the
    /// imported languages away with everything else - which is right, because the C#
    /// index it was merged into no longer exists either.
    ///
    /// --replace is the one thing here that removes rows: it re-imports a .scip over a
    /// previous import of the same file, deleting the documents that file carries and
    /// writing them again. It is asked for by name, it touches only the paths the .scip
    /// itself names, and it says how many documents and occurrences went and how many
    /// came, because a replace that shrinks the index is a fact and a replace nobody
    /// expected is a defect.
    ///
    /// Constraint 3 runs through the whole verb. A file that will not parse leaves the
    /// index exactly as it was and says so; a document that cannot be placed under
    /// vela's root is named and degrades the index; and every way the import came out
    /// smaller than the file it read is counted out loud.
    /// </summary>
    private static Command BuildImportCommand(Option<string> solutionOption)
    {
        var argument = new Argument<string>("index")
        {
            Description = "Path to a .scip file produced by any language's SCIP indexer, "
                        + "for example scip-typescript, scip-python or scip-go."
        };

        // Without this the verb is single-shot. Every document of a second import
        // collides on the UNIQUE relative_path, so nothing is written and the index is
        // degraded: honest, and no use to the only workflow anybody has, which is
        // re-running the indexer after the code changed. There was no way to un-import
        // short of `vela index`, which rebuilds the whole C# half.
        var replaceOption = new Option<bool>("--replace")
        {
            Description = "Import over a previous import of the same .scip: delete the documents this "
                        + "index carries, with their occurrences, and write them again. Only the paths "
                        + "this .scip itself names are touched, whoever contributed them, and how many "
                        + "were replaced is reported."
        };

        var command = new Command("import", "Add a .scip index from another language's indexer")
        {
            argument, solutionOption, replaceOption
        };

        command.SetAction(parseResult =>
        {
            var output = parseResult.InvocationConfiguration.Output;
            var error = parseResult.InvocationConfiguration.Error;

            // Through the same resolver `vela index` uses, so a repository whose vela.json
            // names its solution can import without repeating --solution, and so a config
            // that cannot be honoured stops both verbs in the same place.
            if (!TryResolveSolution(parseResult.GetValue(solutionOption), error, out var solution, out _))
                return ExitCannotAnswer;

            var repositoryRoot = ProjectRoot.ForSolution(solution);
            var scipPath = ResolveScipPath(parseResult.GetRequiredValue(argument), repositoryRoot);
            if (scipPath is null)
            {
                error.WriteLine($"No such file: {parseResult.GetRequiredValue(argument)}");
                error.WriteLine("It was looked for in the current directory and, failing that, under the "
                              + $"repository root {repositoryRoot}, which is where vela index names a "
                              + "job's .scip.");
                return ExitCannotAnswer;
            }

            var path = IndexPaths.ForSolution(solution);
            IndexPaths.EnsureDirectoryExists(path);

            // Importing into nothing is a legitimate thing to want: a repository with no
            // .NET in it at all is still a repository vela can answer about, and
            // requiring a `vela index` run first would mean requiring a solution that
            // does not exist.
            var isNew = !File.Exists(path);

            using var db = new SqliteConnection(ConnectionStringFor(path));
            db.Open();

            if (isNew)
            {
                Schema.Create(db);
            }
            else
            {
                var version = Schema.ReadVersion(db);
                if (version != Schema.Version)
                {
                    error.WriteLine($"The index at {path} was built against schema version {version}, and "
                                  + $"this vela reads version {Schema.Version}. Importing into it would "
                                  + "write rows a later read cannot trust.");
                    error.WriteLine($"Run: vela index --solution {solution}");
                    return ExitCannotAnswer;
                }
            }

            ImportReport report;
            try
            {
                report = ScipImporter.ImportFile(
                    db, scipPath, repositoryRoot, parseResult.GetValue(replaceOption));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Not "could not be read": a file that reads perfectly and declares no
                // project_root is refused too, and telling the user their file is
                // unreadable would send them looking in the wrong place.
                error.WriteLine("The .scip index could not be imported: " + ex.Message);
                error.WriteLine("Nothing was imported, so the index is exactly as it was.");
                return ExitCannotAnswer;
            }

            var tool = string.IsNullOrEmpty(report.Tool) ? "an unnamed indexer" : report.Tool;
            output.WriteLine($"Imported {report.Documents} document(s) and {report.Occurrences} "
                           + $"occurrence(s) from {scipPath}, produced by {tool}, into {path}");

            // The one thing an import can do that removes rows, so it is said first and
            // in numbers. A caller who did not expect to be replacing anything needs to
            // see that they were, and a caller who did needs to see how much went.
            if (report.ReplacedDocuments > 0)
            {
                output.WriteLine($"Replaced {report.ReplacedDocuments} document(s) already in the index: "
                               + $"{report.ReplacedOccurrences} occurrence(s) removed and "
                               + $"{report.ReplacementOccurrences} written in their place.");

                // An index that shrinks is a fact worth reporting rather than an error.
                // It is what re-running the indexer over code that lost a symbol looks
                // like, and it is also what a broken indexer run looks like; the two
                // cannot be told apart from here, so the fact is stated and neither is
                // assumed.
                if (report.ReplacementOccurrences < report.ReplacedOccurrences)
                {
                    output.WriteLine("Those document(s) now hold fewer occurrences than they did. If the "
                                   + "code really lost that much, this is right; if the indexer run was "
                                   + "cut short, the index now matches the shorter run.");
                }

                // What --replace does NOT do. A document a previous import of this file
                // held and this one no longer names is still in the index, because
                // nothing in the database records which import contributed which
                // document, so deleting it would mean deleting on a guess.
                output.WriteLine("Only the paths this .scip names were touched. A document a previous "
                               + "import of it held and this one does not name is still in the index, "
                               + "unchanged: run vela index to rebuild from nothing.");
            }

            // Said plainly and once, in the same voice `vela index` uses for the
            // documents a NuGet package contributed: worth knowing, not a reason to call
            // the index incomplete, and never a reason to raise the exit code.
            if (report.UnconvertedDocuments > 0)
            {
                output.WriteLine($"{report.UnconvertedDocuments} document(s) count character offsets in "
                               + "something other than UTF-16 code units and their text could not be found, "
                               + "so their offsets were stored unconverted. Those are exact on any line of "
                               + "pure ASCII and may be off on a line that is not.");
            }

            // Also plainly, and also never a reason to raise the exit code: nothing is
            // missing, but what unit these offsets are counted in is not stated anywhere
            // in the file, and the reading vela chose is a stated assumption rather than
            // a fact. scip.proto asks indexers not to leave the field unset "so that a
            // consumer can process the SCIP index without ambiguity"; every real
            // scip-typescript index leaves it unset anyway.
            if (report.UnspecifiedEncodingDocuments > 0)
            {
                output.WriteLine($"{report.UnspecifiedEncodingDocuments} document(s) declare no position "
                               + "encoding, so their character offsets were read as UTF-16 code units, which "
                               + "is what every other row in this index means. That is what an indexer "
                               + "written in .NET, Java or TypeScript counts in; an indexer written in Go, "
                               + "Rust, C++ or Python counts in something else, and if one of those left the "
                               + "field unset then columns on lines holding non-ASCII characters are off. The "
                               + "line and the file are right either way.");
            }

            if (report.UnnamedOccurrences > 0)
            {
                output.WriteLine($"{report.UnnamedOccurrences} occurrence(s) carry no symbol at all, so they "
                               + "are in the index and cannot be found by name. SCIP makes the symbol "
                               + "optional, and no name was invented for them.");
            }

            // A name doing double duty, which nothing else in vela can see. The ambiguity
            // block that would otherwise report over-answering groups by exactly this
            // name, so it presents the two symbols as one and the count says nothing.
            if (report.CollidingDisplayNames.Count > 0)
            {
                var names = string.Join(", ", report.CollidingDisplayNames.Take(MaxDetailProblems));
                if (report.CollidingDisplayNames.Count > MaxDetailProblems)
                    names += $", (+{report.CollidingDisplayNames.Count - MaxDetailProblems} more)";

                output.WriteLine($"{report.CollidingDisplayNames.Count} name(s) in this import are reached "
                               + "from more than one distinct SCIP symbol, so a query for one of them answers "
                               + "for both and cannot say it did: " + names + ".");
                output.WriteLine("A module descriptor loses its file extension and any dot still left in a "
                               + "descriptor becomes an underscore, so a folder utils/ beside a file utils.ts, "
                               + "or a module a.b.ts beside a module a_b.ts, arrive at one name - and so do "
                               + "their members. Nothing was lost: each occurrence still carries the moniker "
                               + "it came with, in occurrence.scip_symbol.");
            }

            if (report.UnparsedMonikers > 0)
            {
                output.WriteLine($"{report.UnparsedMonikers} occurrence(s) carry a symbol that does not fit "
                               + "the grammar in scip.proto, so no dotted name could be derived and the "
                               + "symbol itself is the name they are stored under.");
            }

            // An import into nothing needs an indexing-pass record to exist, or every
            // later query reads "index has no health record" and refuses to vouch for an
            // index that is in fact exactly what was asked for. An import into an
            // existing index writes no such record at all: staleness is measured from
            // built_at_utc, and refreshing it would tell a reader the C# half had been
            // compared against the disk when it had not.
            if (isNew) IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, Degraded: false, null));

            // This import's own verdict, under this import's own name, replacing
            // whatever this same file contributed last time. The verb used to OR its
            // degradation into the shared record and append its detail to the text
            // already there, so a lost document degraded every answer forever and every
            // run made the banner longer: a real cache sat at degraded=1 with four
            // duplicate-document entries from an import that had since succeeded. Scoped
            // by source, a cleared problem clears, and this import cannot clear anybody
            // else's.
            //
            // The source is the absolute path <see cref="ResolveScipPath"/> arrived at, so
            // the same file named three different ways from two directories is one
            // contribution rather than three, and it is the same key a pending job is
            // recorded under.
            var detail = report.Degraded ? Summarise(report.Problems) : null;
            IndexHealth.WriteImport(db, scipPath, detail);

            // And the durable record of the import itself, which is what makes it survive
            // the next `vela index`. import_health cannot serve: it holds only the imports
            // that LOST something, and an import that went perfectly is exactly the one a
            // rebuild must not throw away. Written whether or not this one was clean,
            // because a degraded import is still an import and still has to be replayed.
            ImportedSources.Write(db, new ImportedSource(
                scipPath, DateTime.UtcNow, ImportedSources.HashOf(scipPath),
                report.Documents, report.Occurrences));

            if (!report.Degraded) return 0;

            error.WriteLine("!! The imported index is INCOMPLETE. " + detail);
            error.WriteLine("   Answers from it may be missing code. Do not treat an empty result as proof.");
            return IndexHealth.ExitDegraded;
        });

        return command;
    }

    /// <summary>
    /// The .scip the caller means, as an absolute path, or null when there is no such
    /// file anywhere vela is entitled to look.
    ///
    /// <b>Both sides of a pending job have to be resolved the same way.</b> A job's
    /// <see cref="PendingJob.Source"/> is its .scip resolved against the REPOSITORY ROOT,
    /// and the row is cleared under the path this returns, so the two have to agree or
    /// the job can never be settled. They already agreed for an absolute path and for one
    /// typed relative to the directory the caller is standing in, because both name the
    /// same file. They did not agree for the command `vela index` itself prints, which
    /// names the .scip relative to the repository - `vela import src/Mobile/index.scip`.
    /// Run that from `src/`, where anybody who has just run the indexer over `src/Mobile`
    /// is standing, and the file was simply not found: nothing was imported, the job
    /// stayed pending forever, and every answer printed INCOMPLETE naming a command the
    /// user had already run.
    ///
    /// The current directory is tried first, because that is what every command-line tool
    /// does with a path and changing it would break the ordinary case to fix the
    /// unusual one. The repository root is tried only when the first found nothing, so
    /// this can never change which file an existing invocation reads: it can only find
    /// one where there was none.
    /// </summary>
    private static string? ResolveScipPath(string argument, string repositoryRoot)
    {
        if (File.Exists(argument)) return Path.GetFullPath(argument);

        // Path.Combine returns an absolute argument unchanged, so an absolute path that
        // is not there fails here rather than being resolved into something else.
        var underRoot = Path.GetFullPath(Path.Combine(repositoryRoot, argument));
        return File.Exists(underRoot) ? underRoot : null;
    }

    /// <summary>
    /// The health record for a freshly emitted index.
    ///
    /// There are four independent ways for a build to come out incomplete, and all of
    /// them have to reach this record. WorkspaceLoader reports projects that failed to
    /// load. ScipEmitter separately records, into the emitted index's tool arguments,
    /// every document it could not represent because the file lies outside the project
    /// root, every project that produced no compilation at all, and every project that
    /// compiled with errors. A build can hit any of the last three without the first,
    /// so reading only the loader's failures would stamp "healthy" on an index that is
    /// missing whole files, whole projects, or every reference that depended on a type
    /// the compiler could not resolve.
    ///
    /// A document outside the repository altogether is the one thing the emitter
    /// records that is not one of them, and it is deliberately not read here. Nothing
    /// of the user's is missing when the .NET SDK contributes a file from the NuGet
    /// package cache, and calling that index incomplete made every query on a stock
    /// solution exit 3 forever.
    /// </summary>
    public static HealthRecord BuildHealthRecord(Scip.Index index, IReadOnlyList<string> failures) =>
        BuildHealthRecordFromNotes(
            index.Metadata?.ToolInfo?.Arguments ?? (IReadOnlyList<string>)Array.Empty<string>(),
            failures,
            DateTime.UtcNow);

    /// <summary>
    /// The same verdict, reached from the notes rather than from a freshly emitted index.
    ///
    /// An incremental rebuild cannot read its verdict off the index it just emitted,
    /// because that index covers only the projects it harvested. The projects it skipped
    /// still have everything they had to say recorded against them in project_note, and
    /// they are as true as they were: their inputs have not changed and nothing upstream of
    /// them has either. So the two halves are merged and the SAME classification is applied
    /// to both, through the same prefix list, so a note cannot mean one thing on one path
    /// and something else on the other.
    /// </summary>
    /// <param name="builtAtUtc">
    /// Passed in rather than taken here, so the health record, the ledger and the rebuild
    /// summary of one run all carry one instant.
    /// </param>
    public static HealthRecord BuildHealthRecordFromNotes(
        IReadOnlyList<string> notes, IReadOnlyList<string> failures, DateTime builtAtUtc)
    {
        var problems = new List<string>();

        foreach (var note in notes)
        {
            if (ProblemPrefixes.Any(prefix => note.StartsWith(prefix, StringComparison.Ordinal)))
                problems.Add(note);
        }

        // ScipEmitter copies the loader's failures into the index, so they are
        // normally already in the list above and must not be counted twice. They are
        // added here only if the emitted index does not carry them, so that an emit
        // path which drops them cannot quietly produce a healthy-looking index.
        if (!problems.Any(p => p.StartsWith(LoadFailurePrefix, StringComparison.Ordinal)))
        {
            foreach (var failure in failures)
                problems.Add(LoadFailurePrefix + " " + failure);
        }

        return problems.Count == 0
            ? new HealthRecord(builtAtUtc, null, Degraded: false, Detail: null)
            : new HealthRecord(builtAtUtc, null, Degraded: true, Detail: Summarise(problems));
    }

    /// <summary>
    /// How many documents were left out of an index because they belong to somebody
    /// else: source contributed from the NuGet package cache or from the .NET
    /// installation vela is running on.
    ///
    /// This is reported rather than warned about. The count is worth printing, because
    /// a number that is unexpectedly large is worth someone looking at, but it is not a
    /// reason to call the index incomplete and it must never raise the exit code.
    /// </summary>
    public static int CountExternalDocuments(Scip.Index index) => ExternalDocumentPaths(index).Count;

    /// <summary>
    /// Which documents were left out, not merely how many.
    ///
    /// The count alone was not diagnosable. These paths live in the emitted index's tool
    /// arguments, which are never persisted and never written out as SCIP, so `vela
    /// index` printed a number and then destroyed the only record of what it had
    /// counted. They are pulled out here and stored with the index instead.
    /// </summary>
    public static IReadOnlyList<string> ExternalDocumentPaths(Scip.Index index)
    {
        var arguments = index.Metadata?.ToolInfo?.Arguments;
        if (arguments is null) return Array.Empty<string>();

        return arguments
            .Where(a => a.StartsWith(ExternalDocumentPrefix, StringComparison.Ordinal))
            .Select(a => a[ExternalDocumentPrefix.Length..].Trim())
            .ToList();
    }

    private static string Summarise(IReadOnlyList<string> problems)
    {
        var shown = string.Join("; ", problems.Take(MaxDetailProblems));
        return problems.Count > MaxDetailProblems
            ? $"{shown}; (+{problems.Count - MaxDetailProblems} more)"
            : shown;
    }

    /// <summary>
    /// Folds staleness into the health record every verb reads, so an index that is
    /// merely out of date reaches the caller through exactly the same banner and exit
    /// code as one that failed to build.
    ///
    /// The walk starts at the root the index was built against, resolved by the same
    /// ProjectRoot the emitter used, so nothing indexed sits under a tree nothing
    /// watches. Walking the solution directory instead left everything above it indexed
    /// and unwatched. The watched SET is still narrower than the indexed set, because
    /// the walk is bounded to the extensions vela indexes and skips build output, so
    /// absence of a staleness banner is not proof that nothing changed.
    ///
    /// A check that could not run is never reported as a check that passed. If the walk
    /// throws, the record comes back degraded saying so, because the alternative is an
    /// answer that looks freshness-checked and was not, which is the failure Constraint
    /// 3 exists to prevent. The query itself still answers.
    /// </summary>
    private static HealthRecord CheckStaleness(HealthRecord health, string solution)
    {
        try
        {
            return Staleness.Check(health, ProjectRoot.ForSolution(solution), IndexPaths.ForSolution(solution));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var detail = "index freshness could not be checked: " + ex.Message
                       + ". Nothing in this answer has been compared against the code on disk.";

            return health with
            {
                Degraded = true,
                Detail = string.IsNullOrEmpty(health.Detail) ? detail : health.Detail + "; " + detail
            };
        }
    }

    /// <summary>
    /// Opens the index for a solution, or writes the reason it cannot and returns
    /// null. This never calls Environment.Exit: vela's verbs run inside a test host
    /// and inside other people's tooling, and terminating the process from library
    /// code takes the host down with it. The caller turns null into an exit code.
    /// </summary>
    private static SqliteConnection? OpenIndex(string? solution, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(solution))
        {
            error.WriteLine(NoSolutionMessage);
            return null;
        }

        var path = IndexPaths.ForSolution(solution);
        if (!File.Exists(path))
        {
            error.WriteLine($"No index for {solution}. Run: vela index --solution {solution}");
            return null;
        }

        var db = new SqliteConnection(ConnectionStringFor(path));
        db.Open();

        // Constraint 3, applied to the container rather than to its contents. The index
        // is a cache opened by whatever build of vela is on the PATH, so a schema this
        // build does not understand is a routine event, not a corrupt file. Answering
        // from it either throws raw SQL at the user (adding document.generated made
        // every verb fail with "no such column: d.generated") or, on a change that only
        // adds rows, quietly answers from a schema whose columns mean something else.
        // Both are an incomplete index looking like a complete one, so the check comes
        // before the first query and the message names the fix.
        var version = Schema.ReadVersion(db);
        if (version != Schema.Version)
        {
            db.Dispose();

            var built = version == 0
                ? "before vela recorded a schema version in its index"
                : $"against index schema version {version}";

            error.WriteLine($"The index at {path} was built {built}, and this vela reads schema version "
                          + $"{Schema.Version}. It cannot be queried, and answering from it anyway would "
                          + "risk a wrong answer rather than no answer.");
            error.WriteLine("The index is a cache, so it is rebuilt rather than migrated.");
            error.WriteLine($"Run: vela index --solution {solution}");
            return null;
        }

        return db;
    }

    /// <summary>
    /// Built rather than interpolated: an index path contains the solution name, and
    /// a ';' or '=' in it would otherwise be read as connection string syntax.
    ///
    /// Pooling is off deliberately. `vela index` deletes the database file and
    /// rebuilds it, and a pooled connection opened before that delete keeps the old,
    /// now unlinked file alive and hands it straight back to the next open, so the
    /// rebuild both reads and writes a database nobody can see any more. Vela opens
    /// one connection per command, so the pool buys nothing to weigh against that.
    /// </summary>
    private static string ConnectionStringFor(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private static string FindSolution()
    {
        var found = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.sln");
        return found.Length == 1 ? found[0] : "";
    }
}
