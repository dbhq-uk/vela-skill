using System.CommandLine;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// `vela index --incremental`, which is the one thing in vela that can produce a wrong
/// answer rather than a slow one.
///
/// A full rebuild cannot be stale, because it reads everything. An incremental rebuild is
/// a CLAIM that what it skipped has not changed, and if the claim is wrong the index holds
/// rows describing code that no longer exists, at line numbers and under names that have
/// moved, while reporting itself complete. So these tests are mostly about the ways that
/// claim can be false, and every one of them is written against the real command over a
/// real solution rather than against a mock of the decision.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class IncrementalIndexTests
{
    /// <summary>
    /// The test that matters most.
    ///
    /// Edit a public signature in an UPSTREAM project and every reference to it in a
    /// downstream project is now recorded under a name that describes nothing. Not one
    /// file in the downstream project was touched, its own fingerprint is identical, and
    /// the only thing that can select it for rebuild is the closure over the project
    /// reference graph. If that closure is wrong, this is what catches it, and nothing
    /// else here would.
    /// </summary>
    [Fact]
    public async Task Incremental_AfterAnUpstreamEdit_UpdatesAReferenceInADownstreamProject()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        var first = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Lib.Upstream.Twice(System.Int32)", SymbolsIn(fx, "App/Caller.cs"));

        fx.Write("Lib/Upstream.cs", """
            namespace Lib
            {
                public static class Upstream
                {
                    public static long Twice(long value) => value + value;
                }
            }
            """);

        var second = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);
        Assert.Equal(0, second.ExitCode);

        // Two of three: the project that changed, and the one downstream of it. Leaf
        // references nothing and is reached by nothing, so it is reused - and a plan that
        // rebuilt everything would pass every other assertion here.
        Assert.Contains("2 of 3 project(s) rebuilt", second.Output);
        Assert.Contains("Lib/Lib.csproj", second.Output);
        Assert.Contains("App/App.csproj", second.Output);
        Assert.Contains("Leaf/Leaf.csproj", second.Output);

        var symbols = SymbolsIn(fx, "App/Caller.cs");
        Assert.Contains("Lib.Upstream.Twice(System.Int64)", symbols);
        Assert.DoesNotContain("Lib.Upstream.Twice(System.Int32)", symbols);

        // And the index says what it did, durably, rather than only in the output of the
        // run that did it.
        using var db = Open(fx);
        var rebuild = IndexHealth.Read(db).Rebuild;
        Assert.NotNull(rebuild);
        Assert.Contains("Lib/Lib.csproj", rebuild);
        Assert.Contains("Leaf/Leaf.csproj", rebuild);
    }

    [Fact]
    public async Task Incremental_WithNothingChanged_RebuildsNothingAndTheIndexStillAnswers()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var before = Rows(fx, DocumentsAndOccurrences);

        var second = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("0 of 3 project(s) rebuilt", second.Output);

        // Nothing rebuilt has to mean nothing changed, row for row. An incremental run
        // that quietly dropped a document while claiming to have done nothing would be
        // the worst outcome available.
        Assert.Equal(before, Rows(fx, DocumentsAndOccurrences));
        Assert.Contains("Lib.Upstream.Twice(System.Int32)", SymbolsIn(fx, "App/Caller.cs"));
    }

    [Fact]
    public async Task Incremental_AfterALeafEdit_RebuildsOnlyThatProjectAndLeavesNoOrphanedFtsRows()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var found = await InvokeAsync("find", "OriginalOnly", "--solution", fx.SolutionPath);
        Assert.Contains("Leaf.Standalone.OriginalOnly()", found.Output);

        fx.Write("Leaf/Standalone.cs", """
            namespace Leaf
            {
                public static class Standalone
                {
                    public static int RenamedOnly() => 1;
                }
            }
            """);

        var second = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("1 of 3 project(s) rebuilt", second.Output);

        // The full-text table is what `find` searches, and nothing in it is keyed to a
        // document, so a name that has gone survives unless it is actively removed. A
        // find that answers with a symbol no occurrence carries is the discovery verb
        // pointing at code that is not there.
        var gone = await InvokeAsync("find", "OriginalOnly", "--solution", fx.SolutionPath);
        Assert.Contains("0 symbol(s)", gone.Output);

        var renamed = await InvokeAsync("find", "RenamedOnly", "--solution", fx.SolutionPath);
        Assert.Contains("Leaf.Standalone.RenamedOnly()", renamed.Output);

        Assert.Equal(0, Count(fx, "SELECT COUNT(*) FROM symbol_fts WHERE symbol NOT IN "
                               + "(SELECT symbol FROM occurrence)"));
    }

    /// <summary>
    /// The hole a fingerprint opens, and the reason this feature could have gone quiet.
    ///
    /// A project that will not compile is fingerprinted like any other, so an incremental
    /// run can skip it. Its `compile-error:` note, though, is produced fresh by each
    /// harvest and by nothing else, so skipping the project used to mean the note was
    /// never regenerated: the index would stop calling itself degraded while still holding
    /// an incomplete picture of that project. A broken project that goes quiet is exactly
    /// the failure this tool exists to prevent.
    ///
    /// The fix is that each project's problems are recorded against the project, so a
    /// skipped project goes on saying what it could not do. That is honest for the same
    /// reason the skip is: the fingerprint says nothing this project compiles has changed,
    /// and nothing upstream of it has either, so its diagnostics have not changed.
    /// </summary>
    [Fact]
    public async Task Incremental_KeepsTheCompileErrorOfAProjectItSkipped()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        fx.Write("Leaf/Broken.cs", """
            namespace Leaf
            {
                public class Broken : ThisTypeDoesNotExist
                {
                }
            }
            """);

        var first = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(IndexHealth.ExitDegraded, first.ExitCode);
        Assert.Contains("compile-error", first.Output);
        Assert.Contains("'Leaf'", first.Output);

        // An edit that cannot reach Leaf. Leaf references nothing, nothing references
        // Leaf, and its own files are untouched, so it is reused.
        fx.Write("Lib/Upstream.cs", """
            namespace Lib
            {
                public static class Upstream
                {
                    public static long Twice(long value) => value + value;
                }
            }
            """);

        var second = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);
        Assert.Contains("2 of 3 project(s) rebuilt", second.Output);

        // The index must still say it is incomplete, in the same words, for the same
        // project, and with the same exit code.
        Assert.Equal(IndexHealth.ExitDegraded, second.ExitCode);
        Assert.Contains("compile-error", second.Output);
        Assert.Contains("'Leaf'", second.Output);

        using var db = Open(fx);
        var health = IndexHealth.Read(db);
        Assert.True(health.Degraded);
        Assert.Contains("compile-error", health.Detail!);
        Assert.Contains("'Leaf'", health.Detail!);

        // And every query from it carries the banner and the exit code, which is what the
        // record is for.
        var query = await InvokeAsync("refs", "Twice", "--solution", fx.SolutionPath);
        Assert.Equal(IndexHealth.ExitDegraded, query.ExitCode);
        Assert.Contains("INCOMPLETE", query.Output);
    }

    /// <summary>
    /// An imported language must survive an incremental rebuild, exactly as it survives a
    /// full one. It survives here by a different mechanism and the outcome has to be the
    /// same: a full rebuild deletes the database and REPLAYS every .scip into the new one,
    /// while an incremental rebuild never deletes the database and simply does not touch
    /// rows another source contributed.
    /// </summary>
    [Fact]
    public async Task Incremental_LeavesAnImportedLanguageInTheIndex()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var scip = Path.Combine(fx.Root, "site.scip");
        File.WriteAllBytes(scip, ForeignIndex(fx.Root, "site/app.ts", "renderPage").ToByteArray());

        var imported = await InvokeAsync("import", scip, "--solution", fx.SolutionPath);
        Assert.Equal(0, imported.ExitCode);
        Assert.Equal(1, Count(fx, "SELECT COUNT(*) FROM document WHERE relative_path = 'site/app.ts'"));

        fx.Write("Lib/Upstream.cs", """
            namespace Lib
            {
                public static class Upstream
                {
                    public static long Twice(long value) => value + value;
                }
            }
            """);

        var second = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("2 of 3 project(s) rebuilt", second.Output);

        Assert.Equal(1, Count(fx, "SELECT COUNT(*) FROM document WHERE relative_path = 'site/app.ts'"));
        Assert.True(Count(fx, "SELECT COUNT(*) FROM occurrence o JOIN document d ON d.id = o.document_id "
                            + "WHERE d.relative_path = 'site/app.ts'") > 0);

        // Still findable by name, so the full-text table was rebuilt over every source
        // rather than over the harvest alone.
        var found = await InvokeAsync("find", "renderPage", "--solution", fx.SolutionPath);
        Assert.Contains("renderPage", found.Output);

        // And still remembered as an import, so a later FULL rebuild replays it.
        Assert.Equal(1, Count(fx, "SELECT COUNT(*) FROM imported_source"));
    }

    /// <summary>
    /// Two projects compiling one file is ordinary - a linked file, a shared source
    /// directory - and a document in this index holds the occurrences of every project
    /// that compiles it, because documents are keyed by the file a developer can open.
    /// So replacing that document on behalf of one project would delete the other's rows
    /// and nothing would put them back.
    /// </summary>
    [Fact]
    public async Task Incremental_RebuildsEveryProjectThatCompilesAFileItIsReplacing()
    {
        using var fx = FixtureSolution.CreateSharedFileSolution();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var before = SymbolsIn(fx, "Shared/Shared.cs");
        Assert.Contains("Shared.Common.FromAlpha()", before);
        Assert.Contains("Shared.Common.FromBeta()", before);

        // Only Alpha's own file changes. Beta's fingerprint is identical and Beta
        // references nothing, so the reference closure cannot reach it.
        fx.Write("Alpha/Own.cs", """
            namespace Alpha
            {
                public static class Own
                {
                    public static int Value() => 11;
                }
            }
            """);

        var second = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("2 of 3 project(s) rebuilt", second.Output);
        Assert.Contains("Beta/Beta.csproj", second.Output);
        Assert.Contains("Gamma/Gamma.csproj", second.Output);

        var after = SymbolsIn(fx, "Shared/Shared.cs");
        Assert.Contains("Shared.Common.FromAlpha()", after);
        Assert.Contains("Shared.Common.FromBeta()", after);
    }

    [Fact]
    public async Task Incremental_WithNoIndexYet_BuildsTheWholeIndexAndSaysSo()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        var result = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Falling back to a full rebuild", result.Output);
        Assert.Contains("no index", result.Output);
        Assert.Contains("Indexed", result.Output);
    }

    [Fact]
    public async Task Incremental_AfterASchemaChange_FallsBackToAFullRebuildAndSaysSo()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        using (var db = Open(fx))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 7";
            cmd.ExecuteNonQuery();
        }

        var result = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Falling back to a full rebuild", result.Output);
        Assert.Contains("schema", result.Output);
        Assert.Equal(Schema.Version, ReadSchemaVersion(fx));
    }

    [Fact]
    public async Task Incremental_WhenTheProjectSetChanged_FallsBackToAFullRebuildAndSaysSo()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        // A project the ledger remembers and the solution no longer holds. Its documents
        // are still in the database and no per-project rebuild can reach them, because
        // there is no longer a project to name them.
        using (var db = Open(fx))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO project_input(project, name, fingerprint, inputs, schema_version,
                                          vela_version, built_at_utc)
                VALUES ('Gone/Gone.csproj', 'Gone', 'deadbeef', 1, $s, $v, $t)
                """;
            cmd.Parameters.AddWithValue("$s", Schema.Version);
            cmd.Parameters.AddWithValue("$v", ProjectInputs.VelaVersion);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var result = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Falling back to a full rebuild", result.Output);
        Assert.Contains("set of projects changed", result.Output);
    }

    /// <summary>
    /// A plan that selects every project is a full rebuild reached the long way, and it
    /// is NOT the same thing as a whole-index reason firing. The flag on the plan means
    /// "something made the whole index untrustworthy"; this case means "the closure was
    /// right and it happened to reach everything", which is what a change low in the
    /// dependency graph looks like. Both end in a full rebuild and the reader is told
    /// which one happened.
    /// </summary>
    [Fact]
    public async Task Incremental_WhenThePlanSelectsEveryProject_RebuildsTheWholeIndexAndSaysWhy()
    {
        using var fx = FixtureSolution.CreateProjectGraph();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        fx.Write("Lib/Upstream.cs", """
            namespace Lib
            {
                public static class Upstream
                {
                    public static long Twice(long value) => value + value;
                }
            }
            """);
        fx.Write("Leaf/Standalone.cs", """
            namespace Leaf
            {
                public static class Standalone
                {
                    public static int OriginalOnly() => 2;
                }
            }
            """);

        var result = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Falling back to a full rebuild", result.Output);
        Assert.Contains("every project", result.Output);
        Assert.Contains("Indexed", result.Output);
        Assert.Contains("Lib.Upstream.Twice(System.Int64)", SymbolsIn(fx, "App/Caller.cs"));
    }

    [Fact]
    public async Task Index_WithoutTheFlag_RebuildsEverythingAndSaysNothingAboutIncremental()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var second = await InvokeAsync("index", "--solution", fx.SolutionPath);

        Assert.Equal(0, second.ExitCode);
        Assert.Contains("Indexed", second.Output);
        Assert.DoesNotContain("Incremental rebuild", second.Output);
        Assert.DoesNotContain("reused", second.Output);

        // The default is what it always was, to the character, so nothing about this
        // feature can change the answer somebody who did not ask for it gets.
        using var db = Open(fx);
        Assert.Null(IndexHealth.Read(db).Rebuild);
    }

    private const string DocumentsAndOccurrences = """
        SELECT d.relative_path, d.language, d.generated, d.source,
               o.symbol, o.is_definition, o.start_line, o.start_char
        FROM document d LEFT JOIN occurrence o ON o.document_id = d.id
        ORDER BY d.relative_path, o.symbol, o.start_line, o.start_char, o.is_definition
        """;

    private static Scip.Index ForeignIndex(string root, string relativePath, params string[] names)
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                ProjectRoot = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri,
                ToolInfo = new Scip.ToolInfo { Name = "scip-typescript", Version = "0.4.0" }
            }
        };

        var doc = new Scip.Document { RelativePath = relativePath, Language = "typescript" };
        for (var i = 0; i < names.Length; i++)
        {
            doc.Occurrences.Add(new Scip.Occurrence
            {
                Symbol = $"scip-typescript npm app 1.0.0 `app.ts`/{names[i]}().",
                SymbolRoles = (int)Scip.SymbolRole.Definition,
                SingleLineRange = new Scip.SingleLineRange { Line = 3 + i, StartCharacter = 9, EndCharacter = 14 }
            });
        }

        index.Documents.Add(doc);
        return index;
    }

    private static SqliteConnection Open(FixtureSolution fx)
    {
        var path = IndexPaths.ForSolution(fx.SolutionPath);
        var db = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static int ReadSchemaVersion(FixtureSolution fx)
    {
        using var db = Open(fx);
        return Schema.ReadVersion(db);
    }

    private static List<string> SymbolsIn(FixtureSolution fx, string relativePath)
    {
        using var db = Open(fx);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT o.symbol FROM occurrence o JOIN document d ON d.id = o.document_id "
                        + "WHERE d.relative_path = $p ORDER BY o.symbol";
        cmd.Parameters.AddWithValue("$p", relativePath);

        using var reader = cmd.ExecuteReader();
        var symbols = new List<string>();
        while (reader.Read()) symbols.Add(reader.GetString(0));
        return symbols;
    }

    private static List<string> Rows(FixtureSolution fx, string sql)
    {
        using var db = Open(fx);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) values[i] = reader.GetValue(i).ToString() ?? "";
            rows.Add(string.Join('|', values));
        }

        return rows;
    }

    private static int Count(FixtureSolution fx, string sql)
    {
        using var db = Open(fx);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
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

    /// <summary>Points XDG_CACHE_HOME at a disposable directory, and puts it back.</summary>
    private sealed class TempCacheHome : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TempCacheHome()
        {
            _path = Path.Combine(Path.GetTempPath(), "vela-inc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_path);
            _previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _previous);
            try { Directory.Delete(_path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
