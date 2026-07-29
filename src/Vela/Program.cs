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

        root.Add(BuildIndexCommand(solutionOption));
        root.Add(BuildFindCommand(solutionOption));
        root.Add(BuildHitCommand("def", "Where a symbol is defined",
            "symbol", "Symbol name, or a suffix of one, for example Perfume.Status.",
            solutionOption, DefQuery.Run, DefQuery.ExplainEmpty));
        root.Add(BuildHitCommand("refs", "Every usage of a symbol",
            "symbol", "Symbol name, or a suffix of one, for example Perfume.Status.",
            solutionOption, RefsQuery.Run, RefsQuery.ExplainEmpty));
        root.Add(BuildHitCommand("outline", "Symbols defined in a file",
            "file", "Path of the file, relative to the solution directory.",
            solutionOption, OutlineQuery.Run, OutlineQuery.ExplainEmpty));
        root.Add(BuildHitCommand("impact", "Callers and blast radius",
            "symbol", "Symbol name, or a suffix of one, for example Perfume.Status.",
            solutionOption, ImpactQuery.Run, ImpactQuery.ExplainEmpty));

        return root;
    }

    /// <summary>
    /// One of the four verbs that answer with a list of hits. They differ only in
    /// which query they run, in what their single argument means, and in what an
    /// empty answer from them can honestly be said to mean.
    /// </summary>
    private static Command BuildHitCommand(
        string name, string description,
        string argumentName, string argumentDescription,
        Option<string> solutionOption,
        Func<SqliteConnection, string, IReadOnlyList<Hit>> run,
        Func<SqliteConnection, string, string> explainEmpty)
    {
        var argument = new Argument<string>(argumentName) { Description = argumentDescription };
        var command = new Command(name, description) { argument, solutionOption };

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
            var hits = run(db, value);

            // The reason is worked out only when there is nothing to report, so the
            // normal answer costs no extra query.
            var explanation = hits.Count == 0 ? explainEmpty(db, value) : null;

            output.Write(OutputWriter.Render(hits, health, explanation));
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
        var command = new Command("index", "Build the index for a solution") { solutionOption };

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
            var index = await Vela.Harvest.ScipEmitter.EmitAsync(load.Solution, load.Failures, cancellationToken);
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
                ScipLoader.Load(db, index);
                IndexHealth.Write(db, health);
            }

            output.WriteLine($"Indexed {index.Documents.Count} documents to {path}");

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
    /// The walk is best effort by design. If the solution directory cannot be read at
    /// all, the record is returned unchanged rather than thrown from: failing a query
    /// because the freshness check could not run would be a worse outcome than the
    /// answer it was checking.
    /// </summary>
    private static HealthRecord CheckStaleness(HealthRecord health, string solution)
    {
        try
        {
            return Staleness.Check(health, solution, IndexPaths.ForSolution(solution));
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
