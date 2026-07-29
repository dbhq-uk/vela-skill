using System.CommandLine;
using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

// Indexing through the CLI resolves the index path from XDG_CACHE_HOME, which is
// process-wide, so this class shares the non-parallel collection with every other test
// that touches it.
[Collection(EnvironmentSensitive.Name)]
public class EndToEndTests
{
    [Fact]
    public async Task IndexThenRefs_FindsASymbolUsedFromARazorView()
    {
        using var fx = FixtureSolution.CreateWebApp();

        // The scaffolded Index.cshtml uses ViewData, which is declared in C#.
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        Assert.Empty(load.Failures);

        var emitted = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, emitted.Index, emitted.GeneratedDocuments);
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));

        var razorHits = RefsQuery.Run(db, "ViewData")
            .Where(h => h.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(razorHits);
        // The location must be openable: a .cshtml path, not a .g.cs one.
        Assert.All(razorHits, h => Assert.DoesNotContain(".g.cs", h.RelativePath));
    }

    [Fact]
    public async Task IndexWithStats_ReportsTheCoverageThatMustNotRegress()
    {
        // AGENTS.md prescribes `vela index --stats` as the way to check the property
        // this whole tool exists for, and there was no such option: the instruction
        // named a command nobody could have run.
        //
        // The property itself is why the option is worth having. Razor views and Blazor
        // components reach the compiler as source-generated documents, and a regression
        // that loses them is silent - the index still builds, queries still answer, and
        // the Razor half of the codebase quietly disappears. So the numbers are
        // asserted here, by count, through the real command.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var result = await InvokeAsync("index", "--stats", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("documents", result.Output, StringComparison.Ordinal);
        Assert.Contains("generated", result.Output, StringComparison.Ordinal);
        Assert.Contains("razor", result.Output, StringComparison.Ordinal);
        Assert.Contains("occurrences", result.Output, StringComparison.Ordinal);
        Assert.Contains("definitions", result.Output, StringComparison.Ordinal);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        Assert.True(File.Exists(indexPath), result.Output);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = indexPath, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();

        var stats = IndexStatistics.Read(db);

        // One document per view, and the fixture must actually have views or the
        // assertion above is vacuous.
        Assert.True(fx.RazorFileCount > 0, "fixture must contain .cshtml files");
        Assert.Equal(fx.RazorFileCount, stats.RazorDocuments);

        // Seeded but empty Razor documents would satisfy the count above, so the views
        // have to carry occurrences too.
        Assert.True(stats.RazorOccurrences > 0,
            "Razor documents exist but carry no occurrences, which is the whole point of the tool");

        Assert.True(stats.GeneratedDocuments > 0, "the Razor generator's output must be indexed and marked");
        Assert.True(stats.Documents > stats.RazorDocuments);
        Assert.True(stats.Definitions > 0);
        Assert.True(stats.Occurrences > stats.Definitions);
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
            _path = Path.Combine(Path.GetTempPath(), "vela-e2e-" + Guid.NewGuid().ToString("N")[..8]);
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
