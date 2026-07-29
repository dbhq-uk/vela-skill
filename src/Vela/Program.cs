using System.CommandLine;
using Microsoft.Data.Sqlite;
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
    /// </summary>
    private const int MaxDetailProblems = 10;

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

        var command = new Command("index", "Build the index for a solution") { solutionOption, statsOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var output = parseResult.InvocationConfiguration.Output;
            var error = parseResult.InvocationConfiguration.Error;

            var solution = parseResult.GetValue(solutionOption);
            if (string.IsNullOrWhiteSpace(solution))
            {
                error.WriteLine(NoSolutionMessage);
                return ExitCannotAnswer;
            }

            var load = await Vela.Harvest.WorkspaceLoader.LoadAsync(solution, cancellationToken);
            var emitted = await Vela.Harvest.ScipEmitter.EmitAsync(load.Solution, load.Failures, cancellationToken);
            var index = emitted.Index;
            var health = BuildHealthRecord(index, load.Failures);

            var path = IndexPaths.ForSolution(solution);

            // ForSolution is pure, so the cache directory may not exist yet, and
            // ScipLoader.Load requires an empty schema: re-indexing into the last
            // build's database is a precondition violation, not an update.
            IndexPaths.EnsureDirectoryExists(path);
            if (File.Exists(path)) File.Delete(path);

            using (var db = new SqliteConnection(ConnectionStringFor(path)))
            {
                db.Open();
                Schema.Create(db);
                ScipLoader.Load(db, index, emitted.GeneratedDocuments);

                var external = ExternalDocumentPaths(index);
                ExternalDocuments.Write(db, external);
                IndexHealth.Write(db, health);

                output.WriteLine($"Indexed {index.Documents.Count} documents to {path}");

                // Said plainly, and once. These files are not in the index and the
                // number is worth knowing, but they were never this index's to hold, so
                // saying it through the "!!" banner below would raise the exit code and
                // teach the reader to ignore the banner (Constraint 3 cuts both ways).
                //
                // The sentence claims only what was checked. A document reaches this
                // count by living under the NuGet package cache or under the .NET
                // installation, and nothing else does: a file merely outside the
                // repository is first-party code until shown otherwise and goes to the
                // banner. So naming those two places is exact, where "from outside this
                // repository" was a wider claim than the test behind it.
                if (external.Count > 0)
                {
                    output.WriteLine($"{external.Count} document(s) contributed by a NuGet package or "
                                   + "the .NET SDK were not indexed. They live in the package cache or "
                                   + "the .NET installation, not in this repository, so none of your "
                                   + "code is missing because of them. Run vela index --stats to list "
                                   + "them.");
                }

                if (parseResult.GetValue(statsOption))
                    output.Write(IndexStatistics.Render(IndexStatistics.Read(db)));
            }

            if (health.Degraded)
            {
                error.WriteLine("!! The index is INCOMPLETE. " + health.Detail);
                error.WriteLine("   Answers from it may be missing code. Do not treat an empty result as proof.");
                return IndexHealth.ExitDegraded;
            }

            return 0;
        });

        return command;
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
    public static HealthRecord BuildHealthRecord(Scip.Index index, IReadOnlyList<string> failures)
    {
        var problems = new List<string>();

        var arguments = index.Metadata?.ToolInfo?.Arguments;
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                if (ProblemPrefixes.Any(prefix => argument.StartsWith(prefix, StringComparison.Ordinal)))
                    problems.Add(argument);
            }
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
            ? new HealthRecord(DateTime.UtcNow, null, Degraded: false, Detail: null)
            : new HealthRecord(DateTime.UtcNow, null, Degraded: true, Detail: Summarise(problems));
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
    /// ProjectRoot the emitter used, so the files that are watched are exactly the files
    /// that are indexed. Walking the solution directory instead left everything above it
    /// indexed and unwatched.
    ///
    /// The walk is best effort by design. If the root cannot be read at all, the record
    /// is returned unchanged rather than thrown from: failing a query because the
    /// freshness check could not run would be a worse outcome than the answer it was
    /// checking.
    /// </summary>
    private static HealthRecord CheckStaleness(HealthRecord health, string solution)
    {
        try
        {
            return Staleness.Check(health, ProjectRoot.ForSolution(solution), IndexPaths.ForSolution(solution));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return health;
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
