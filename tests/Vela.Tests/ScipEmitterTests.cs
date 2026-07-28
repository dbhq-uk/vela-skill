using Vela.Harvest;
using Vela.Tests.Fixtures;
using Xunit;

public class ScipEmitterTests
{
    [Fact]
    public async Task EmitAsync_ProducesADocumentForEveryRazorView()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        var razorDocs = index.Documents.Count(d =>
            d.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(fx.RazorFileCount, razorDocs);
    }

    [Fact]
    public async Task EmitAsync_RecordsEnclosingRangeOnDefinitions()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        var definitionsWithEnclosure = index.Documents
            .SelectMany(d => d.Occurrences)
            .Count(o => o.EnclosingRange.Count > 0);

        Assert.True(definitionsWithEnclosure > 0,
            "enclosing_range is what makes callers a stored edge rather than an inference");
    }
}
