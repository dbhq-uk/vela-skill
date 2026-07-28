using System.CommandLine;
using Microsoft.Data.Sqlite;
using Vela.Indexing;
using Vela.Query;
using Xunit;

/// <summary>
/// Tests that mutate process-wide state (XDG_CACHE_HOME) belong to this collection so
/// they can never run beside each other. xUnit runs collections in parallel by
/// default, and an environment variable is shared by every test in the process.
/// </summary>
[CollectionDefinition(EnvironmentSensitive.Name, DisableParallelization = true)]
public class EnvironmentSensitive
{
    public const string Name = "environment-sensitive";
}

[Collection(EnvironmentSensitive.Name)]
public class QueryTests
{
    private static SqliteConnection SeededDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document(id, relative_path, language) VALUES
                (1, 'App/Models/Perfume.cs', 'csharp'),
                (2, 'App/Pages/Index.cshtml', 'razor');
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                (1, 'App.Models.Perfume.Status', 1, 10, 4, 12, 5),
                (2, 'App.Models.Perfume.Status', 0, 7, 12, NULL, NULL),
                (1, 'App.Models.Perfume.Name',   1, 20, 4, 22, 5);
            INSERT INTO symbol_fts(symbol) VALUES
                ('App.Models.Perfume.Status'), ('App.Models.Perfume.Name');
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", false, null));
        return db;
    }

    [Fact]
    public void Refs_ReturnsBothCSharpAndRazorOccurrences()
    {
        using var db = SeededDb();
        var hits = RefsQuery.Run(db, "Perfume.Status");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.RelativePath.EndsWith(".cshtml"));
    }

    [Fact]
    public void Def_ReturnsOnlyTheDefinition()
    {
        using var db = SeededDb();
        var hits = DefQuery.Run(db, "Perfume.Status");

        Assert.Single(hits);
        Assert.True(hits[0].IsDefinition);
        Assert.Equal(10, hits[0].Line);
    }

    [Fact]
    public void Outline_ReturnsDefinitionsInOneFile()
    {
        using var db = SeededDb();
        var hits = OutlineQuery.Run(db, "App/Models/Perfume.cs");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.IsDefinition));
    }

    [Fact]
    public void Find_MatchesPartialSymbolNames()
    {
        using var db = SeededDb();
        var symbols = FindQuery.Run(db, "Status");

        Assert.Contains("App.Models.Perfume.Status", symbols);
    }

    /// <summary>
    /// A database whose symbols contain characters that mean something to SQL LIKE
    /// and to FTS5, so a query for one of them cannot quietly match the other.
    /// </summary>
    private static SqliteConnection PunctuationDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document(id, relative_path, language) VALUES
                (1, 'App/Models/Perfume.cs', 'csharp');
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                (1, 'App.Models.Perfume.Foo_Bar', 1, 1, 4, NULL, NULL),
                (1, 'App.Models.Perfume.FooXBar', 1, 2, 4, NULL, NULL);
            INSERT INTO symbol_fts(symbol) VALUES
                ('App.Models.Perfume.Foo_Bar'), ('App.Models.Perfume.FooXBar');
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", false, null));
        return db;
    }

    [Fact]
    public void Refs_TreatsUnderscoreAndPercentInThePatternAsLiterals()
    {
        // SQL LIKE reads '_' as "any one character" and '%' as "any run of
        // characters". Underscores are ordinary in .NET identifiers, so without an
        // ESCAPE clause a search for Foo_Bar silently also answers for FooXBar, and
        // the caller has no way to tell the extra hit is not real.
        using var db = PunctuationDb();

        var hits = RefsQuery.Run(db, "Foo_Bar");

        Assert.Single(hits);
        Assert.Equal("App.Models.Perfume.Foo_Bar", hits[0].Symbol);
    }

    [Fact]
    public void Find_TreatsThePatternAsTextRatherThanAsFts5Syntax()
    {
        // FTS5 MATCH has its own query language: '.', '(', '"' and the bare words
        // AND/OR/NOT all mean something in it. A symbol name pasted straight in is
        // therefore either a syntax error or, worse, a different query than asked
        // for. Every one of these is a plausible thing to paste after `vela find`.
        using var db = SeededDb();

        Assert.Contains("App.Models.Perfume.Status", FindQuery.Run(db, "Perfume.Status"));
        Assert.Contains("App.Models.Perfume.Status", FindQuery.Run(db, "Status("));
        Assert.Empty(FindQuery.Run(db, "\"unbalanced"));
        Assert.Empty(FindQuery.Run(db, "NOT"));
    }

    [Fact]
    public void Impact_ReturnsTheDefinitionEnclosingEachReference()
    {
        // The blast radius of a change: which symbols contain a reference to the
        // target, worked out from the stored enclosing ranges.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Services/PerfumeService.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Services.PerfumeService.Publish()', 1, 30, 4, 40, 5),
                    (1, 'App.Services.PerfumeService.Archive()', 1, 50, 4, 60, 5),
                    (1, 'App.Models.Perfume.Status', 0, 32, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var hits = ImpactQuery.Run(db, "Perfume.Status");

        Assert.Single(hits);
        Assert.Equal("App.Services.PerfumeService.Publish()", hits[0].Symbol);
        Assert.Equal(30, hits[0].Line);
    }

    [Fact]
    public void Render_OnDegradedIndex_SaysSoInTheOutput()
    {
        using var db = SeededDb();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc", true, "App.csproj failed to load"));

        var output = OutputWriter.Render(RefsQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        Assert.Contains("INCOMPLETE", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("App.csproj", output);
    }

    [Fact]
    public void Render_GroupsHitsByFile()
    {
        using var db = SeededDb();
        var output = OutputWriter.Render(RefsQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        Assert.Contains("App/Pages/Index.cshtml", output);
        Assert.Contains("App/Models/Perfume.cs", output);
    }

    [Fact]
    public void Render_ConvertsStoredZeroBasedPositionsToOneBased()
    {
        // Positions are stored exactly as Roslyn produced them, which is zero-based.
        // Every editor, compiler diagnostic and human counts from one, so a
        // rendered position that is off by one sends the reader to the wrong line.
        using var db = SeededDb();
        var output = OutputWriter.Render(DefQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        // Stored (10, 4) is line 11, column 5.
        Assert.Contains("11:5", output);
        Assert.DoesNotContain("10:4", output);
    }

    [Fact]
    public void Render_OnDegradedIndex_WarnsThatAShortResultIsNotProof()
    {
        // Constraint 3: the dangerous reading of a degraded index is the empty
        // result, because "no hits" and "no hits I could see" look identical.
        using var db = SeededDb();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc", true, "App.csproj failed to load"));

        var output = OutputWriter.Render(Array.Empty<Hit>(), IndexHealth.Read(db));

        Assert.Contains("not treat an empty or short result as proof", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- CLI wiring -------------------------------------------------------
    //
    // These exercise the built command tree end to end, because the thing that has
    // to be true of a degraded index is not that the renderer can print a banner,
    // it is that a caller redirecting stdout still learns the answer is incomplete.
    // That signal is the process exit code, and it has to survive parsing,
    // invocation and the return path.

    [Fact]
    public async Task DegradedIndex_ExitsThree_AndHealthyIndexExitsZero()
    {
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);

        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, true, "App.csproj failed to load"));
        var degraded = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(IndexHealth.ExitDegraded, degraded.ExitCode);
        Assert.Contains("INCOMPLETE", degraded.Output, StringComparison.Ordinal);
        Assert.Contains("App/Models/Perfume.cs", degraded.Output, StringComparison.Ordinal);

        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, false, null));
        var healthy = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(0, healthy.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", healthy.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindOnADegradedIndex_AlsoExitsThree()
    {
        // find returns a list of names rather than hits, but an incomplete index
        // makes its answer just as incomplete, so it carries the same signal.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, true, "App.csproj failed to load"));

        var result = await InvokeAsync("find", "Status", "--solution", solution);

        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("INCOMPLETE", result.Output, StringComparison.Ordinal);
        Assert.Contains("App.Models.Perfume.Status", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingIndex_ExitsNonZeroWithoutKillingTheProcess()
    {
        // The failure path here must return an exit code, never call
        // Environment.Exit: library code that exits the process would take this test
        // host, and anything else hosting vela, down with it. If that regressed,
        // this test would not fail, the whole run would vanish.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var result = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("vela index", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHealthRecord_FoldsInProblemsRecordedByTheEmitter()
    {
        // ScipEmitter records two kinds of problem into the emitted index, and both
        // mean the index is missing code: projects that failed to load, and
        // documents that fall outside the project root and so cannot be represented
        // in SCIP at all. Reading only the loader's failure list would report a
        // complete index while whole files were silently absent from it.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ToolInfo = new Scip.ToolInfo { Name = "vela", Version = "0.0.0" } }
        };
        index.Metadata.ToolInfo.Arguments.Add("outside-project-root: /nuget/RazorLib/Views/Shared.cshtml");

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());

        Assert.True(health.Degraded);
        Assert.Contains("Shared.cshtml", health.Detail);
    }

    [Fact]
    public void BuildHealthRecord_IsCleanWhenNothingWentWrong()
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ToolInfo = new Scip.ToolInfo { Name = "vela", Version = "0.0.0" } }
        };

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());

        Assert.False(health.Degraded);
        Assert.Null(health.Detail);
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        // Output and Error are captured through InvocationConfiguration rather than
        // Console.SetOut, so the capture is scoped to this invocation instead of the
        // whole process. EnableDefaultExceptionHandler is off so that an exception
        // reaches the test as an exception rather than as an exit code.
        using var writer = new StringWriter();
        var configuration = new InvocationConfiguration
        {
            Output = writer,
            Error = writer,
            EnableDefaultExceptionHandler = false
        };

        var exitCode = await Program.BuildRootCommand().Parse(args).InvokeAsync(configuration);
        return (exitCode, writer.ToString());
    }

    private static void WriteIndexFile(string path, HealthRecord health)
    {
        if (File.Exists(path)) File.Delete(path);

        // Pooling off for the same reason vela turns it off: this helper deletes and
        // recreates the file between invocations, and a pooled handle to the deleted
        // file would be served back in place of the new one.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Models/Perfume.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Models.Perfume.Status', 1, 10, 4, 12, 5);
                INSERT INTO symbol_fts(symbol) VALUES ('App.Models.Perfume.Status');
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, health);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vela-q-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    /// <summary>Points XDG_CACHE_HOME somewhere disposable, and puts it back.</summary>
    private sealed class CacheHome : IDisposable
    {
        private readonly string? _previous;

        public CacheHome(string path)
        {
            _previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", path);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _previous);
    }
}
