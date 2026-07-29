using System.CommandLine;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

// `vela import` resolves the index path from XDG_CACHE_HOME, which is process-wide, so
// this class shares the non-parallel collection with everything else that touches it.
[Collection(EnvironmentSensitive.Name)]
public class ScipImportEndToEndTests
{
    /// <summary>
    /// The strongest single test of the feature: what vela emits, read back through the
    /// importer, has to answer the same questions.
    ///
    /// It is an equality of ANSWERS and not of stored strings, and it could not honestly
    /// be anything else. A moniker does not carry a method's parameter list, so the
    /// direct load stores App.Pages.IndexModel.OnGet() where the import derives
    /// App.Pages.IndexModel.OnGet. Both are found by `refs OnGet`, which is the question
    /// anyone asks, and the hits - file, line, character, definition or not - are
    /// identical. See <see cref="MonikerName"/> for the three places the derived name is
    /// deliberately shorter than the Roslyn one.
    /// </summary>
    [Fact]
    public async Task RoundTrip_AnImportedIndexAnswersTheSameQueriesAsTheDirectLoad()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        Assert.Empty(load.Failures);

        var emitted = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);
        var healthy = new HealthRecord(DateTime.UtcNow, null, false, null);

        using var direct = new SqliteConnection("Data Source=:memory:");
        direct.Open();
        Schema.Create(direct);
        ScipLoader.Load(direct, emitted);
        IndexHealth.Write(direct, healthy);

        var scip = Path.Combine(Path.GetTempPath(), "vela-roundtrip-" + Guid.NewGuid().ToString("N")[..8] + ".scip");
        File.WriteAllBytes(scip, emitted.Index.ToByteArray());

        try
        {
            using var imported = new SqliteConnection("Data Source=:memory:");
            imported.Open();
            Schema.Create(imported);
            var report = ScipImporter.ImportFile(imported, scip, ProjectRoot.ForSolution(fx.SolutionPath));
            IndexHealth.Write(imported, healthy);

            Assert.False(report.Degraded, string.Join("; ", report.Problems));

            // Same documents, in the same places, with the same generated flags. The
            // generated flag is the one vela invented a column for and SCIP carries as
            // SymbolRole.Generated, so losing it here would mean refs and impact
            // suppressing a different set of files on the two sides.
            Assert.Equal(
                Rows(direct, "SELECT relative_path, language, generated FROM document ORDER BY relative_path"),
                Rows(imported, "SELECT relative_path, language, generated FROM document ORDER BY relative_path"));

            // Same occurrences, at the same positions, in the same roles.
            Assert.Equal(
                Rows(direct, PositionsSql),
                Rows(imported, PositionsSql));

            // And the queries themselves. Each of these is a symbol the fixture really
            // has, and each exercises a different shape: a property reached from a Razor
            // view, a method, a type, a generic type whose arity only the moniker
            // carries, and an interface from a package.
            foreach (var symbol in new[] { "ViewData", "OnGet", "IndexModel", "ILogger", "Configure", "Main" })
            {
                Assert.Equal(Answer(direct, symbol), Answer(imported, symbol));
                Assert.Equal(Definitions(direct, symbol), Definitions(imported, symbol));
            }

            // A vacuous pass would be two empty answers, so at least one of them has to
            // have found something.
            Assert.NotEmpty(Answer(imported, "ViewData"));

            // outline names a file, not a symbol, so it is the query most exposed to a
            // path that was rebased wrongly.
            var razor = Rows(direct, "SELECT relative_path FROM document WHERE relative_path LIKE '%.cshtml'").First();
            Assert.Equal(
                OutlineQuery.Run(direct, razor).Select(h => (h.RelativePath, h.Line, h.Character)),
                OutlineQuery.Run(imported, razor).Select(h => (h.RelativePath, h.Line, h.Character)));
        }
        finally
        {
            File.Delete(scip);
        }
    }

    /// <summary>
    /// A .scip file scip-typescript 0.4.0 actually produced, over
    /// /home/devops/scentverdict/src/ScentVerdict.Mobile, trimmed to four of its 25
    /// documents and otherwise byte-for-byte what the indexer wrote. Nothing in it was
    /// authored for this test, which is the point: it declares no position encoding, it
    /// declares no language, it fills in neither display_name nor enclosing_symbol on
    /// any symbol, and two of its four documents each carry a `local 1`. Every one of
    /// those is a thing the specification permits and a hand-built fixture would not
    /// have thought to do.
    /// </summary>
    [Fact]
    public void Import_ReadsAnIndexARealTypeScriptIndexerProduced()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        // The index is rooted at the mobile app; vela's root is the repository above it.
        var report = ScipImporter.ImportFile(db, RealTypeScriptIndex, "/home/devops/scentverdict");
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));

        Assert.Equal("scip-typescript", report.Tool);
        Assert.Equal(4, report.Documents);
        Assert.Equal(702, report.Occurrences);
        Assert.False(report.Degraded, string.Join("; ", report.Problems));

        // Hazard 3: every path is rebased onto vela's root, so a hit can be opened from
        // the repository rather than from the directory the indexer happened to run in.
        Assert.Equal(
            new[]
            {
                "src/ScentVerdict.Mobile/src/composables/useAffiliateBrowser.ts",
                "src/ScentVerdict.Mobile/src/composables/useApi.ts",
                "src/ScentVerdict.Mobile/src/composables/useAppReview.ts",
                "src/ScentVerdict.Mobile/src/utils/drydown.ts"
            },
            Rows(db, "SELECT relative_path FROM document ORDER BY relative_path"));

        // The index says nothing about what language any of these are written in.
        Assert.Equal(4, Count(db, "SELECT COUNT(*) FROM document WHERE language = 'typescript'"));

        // Hazard 2: two of the four documents define a `local 1`, and they are two
        // different variables. Fused, refs on one would answer with both.
        Assert.Equal(2, Count(db,
            "SELECT COUNT(*) FROM occurrence WHERE scip_symbol = 'local 1' AND is_definition = 1"));
        Assert.Equal(2, Count(db,
            "SELECT COUNT(DISTINCT symbol) FROM occurrence WHERE scip_symbol = 'local 1'"));

        // The display name derivation, on a symbol nobody wrote for a test: drydown.ts
        // exports phaseToOnset, and `refs` has to reach it by the name a person types.
        var hits = RefsQuery.Run(db, "phaseToOnset", includeGenerated: false);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("src/ScentVerdict.Mobile/src/utils/drydown.ts", h.RelativePath));
        Assert.Contains(hits, h => h.Symbol == "src.utils.drydown.ts.phaseToOnset");
        Assert.Contains(hits, h => h.IsDefinition);

        // A type from the same file, reached the same way, and its property.
        Assert.NotEmpty(RefsQuery.Run(db, "Onset", includeGenerated: false));
        Assert.NotEmpty(RefsQuery.Run(db, "Onset.top", includeGenerated: false));

        // The qualified form still works, and the module descriptor keeps the extension
        // scip-typescript put in it.
        Assert.NotEmpty(RefsQuery.Run(db, "drydown.ts.phaseToOnset", includeGenerated: false));

        // A name from another file must not be dragged in by the one above.
        Assert.Empty(RefsQuery.Run(db, "phaseToOnsetX", includeGenerated: false));
    }

    [Fact]
    public async Task Import_ThroughTheCli_AddsALanguageBesideTheCsharpIndex()
    {
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var indexed = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(0, indexed.ExitCode);

        // A tiny index rooted at the fixture, standing in for a second indexer's output.
        var foreign = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                ProjectRoot = new Uri(fx.Root + Path.DirectorySeparatorChar).AbsoluteUri,
                ToolInfo = new Scip.ToolInfo { Name = "scip-typescript", Version = "0.4.0" }
            }
        };
        var doc = new Scip.Document { RelativePath = "App/wwwroot/js/site.ts" };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = "scip-typescript npm app 1.0.0 `site.ts`/greet().",
            SymbolRoles = (int)Scip.SymbolRole.Definition,
            SingleLineRange = new Scip.SingleLineRange { Line = 3, StartCharacter = 9, EndCharacter = 14 }
        });
        foreign.Documents.Add(doc);

        var scip = Path.Combine(fx.Root, "foreign.scip");
        File.WriteAllBytes(scip, foreign.ToByteArray());

        var imported = await InvokeAsync("import", scip, "--solution", fx.SolutionPath);
        Assert.Equal(0, imported.ExitCode);
        Assert.Contains("scip-typescript", imported.Output, StringComparison.Ordinal);
        Assert.Contains("1 document", imported.Output, StringComparison.Ordinal);

        // The C# is still there and the TypeScript is now beside it, in one index, and
        // one query reaches the new language.
        var refs = await InvokeAsync("refs", "greet", "--solution", fx.SolutionPath);
        Assert.Contains("site.ts", refs.Output, StringComparison.Ordinal);

        var csharp = await InvokeAsync("refs", "ViewData", "--solution", fx.SolutionPath);
        Assert.Contains(".cshtml", csharp.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_ThroughTheCli_SaysSoAndDegradesWhenAFileCannotBeRead()
    {
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var rubbish = Path.Combine(fx.Root, "rubbish.scip");
        File.WriteAllBytes(rubbish, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff });

        var result = await InvokeAsync("import", rubbish, "--solution", fx.SolutionPath);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("Nothing was imported", result.Output, StringComparison.Ordinal);

        // The index it was pointed at is exactly as it was, so the C# still answers.
        var csharp = await InvokeAsync("refs", "ViewData", "--solution", fx.SolutionPath);
        Assert.Equal(0, csharp.ExitCode);
        Assert.Contains(".cshtml", csharp.Output, StringComparison.Ordinal);
    }

    private const string PositionsSql = """
        SELECT d.relative_path, o.start_line, o.start_char, o.is_definition,
               IFNULL(o.enc_end_line, -1), IFNULL(o.enc_end_char, -1)
        FROM occurrence o JOIN document d ON d.id = o.document_id
        ORDER BY d.relative_path, o.start_line, o.start_char, o.is_definition
        """;

    private static readonly string RealTypeScriptIndex =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "scip-typescript-0.4.0-ScentVerdict.Mobile.scip");

    private static List<(string Path, int Line, int Character, bool IsDefinition)> Answer(
        SqliteConnection db, string symbol) =>
        RefsQuery.Run(db, symbol, includeGenerated: true)
            .Select(h => (h.RelativePath, h.Line, h.Character, h.IsDefinition))
            .OrderBy(h => h.RelativePath, StringComparer.Ordinal)
            .ThenBy(h => h.Line)
            .ThenBy(h => h.Character)
            .ToList();

    private static List<(string Path, int Line, int Character)> Definitions(
        SqliteConnection db, string symbol) =>
        DefQuery.Run(db, symbol)
            .Select(h => (h.RelativePath, h.Line, h.Character))
            .OrderBy(h => h.RelativePath, StringComparer.Ordinal)
            .ThenBy(h => h.Line)
            .ThenBy(h => h.Character)
            .ToList();

    private static List<string> Rows(SqliteConnection db, string sql)
    {
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

    private static int Count(SqliteConnection db, string sql)
    {
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
        private readonly string? previous;
        private readonly string path;

        public TempCacheHome()
        {
            path = Path.Combine(Path.GetTempPath(), "vela-import-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(path);
            previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
            try { Directory.Delete(path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
