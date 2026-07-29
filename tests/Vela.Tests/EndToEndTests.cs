using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

public class EndToEndTests
{
    [Fact]
    public async Task IndexThenRefs_FindsASymbolUsedFromARazorView()
    {
        using var fx = FixtureSolution.CreateWebApp();

        // The scaffolded Index.cshtml uses ViewData, which is declared in C#.
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        Assert.Empty(load.Failures);

        var index = (await ScipEmitter.EmitAsync(load.Solution, load.Failures, default)).Index;

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));

        var razorHits = RefsQuery.Run(db, "ViewData")
            .Where(h => h.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(razorHits);
        // The location must be openable: a .cshtml path, not a .g.cs one.
        Assert.All(razorHits, h => Assert.DoesNotContain(".g.cs", h.RelativePath));
    }
}
