using Vela.Harvest;
using Vela.Tests.Fixtures;
using Xunit;

public class DocumentEnumeratorTests
{
    [Fact]
    public async Task EnumerateAsync_IncludesOneGeneratedDocumentPerCshtml()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var project = load.Solution.Projects.Single();

        var docs = new List<HarvestedDocument>();
        await foreach (var d in DocumentEnumerator.EnumerateAsync(project, default))
            docs.Add(d);

        var razorGenerated = docs.Count(d => d.IsGenerated &&
            d.GeneratedPath.Contains("cshtml", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(fx.RazorFileCount, razorGenerated);
        Assert.True(fx.RazorFileCount > 0, "fixture must contain .cshtml files");
    }

    [Fact]
    public async Task EnumerateAsync_IncludesBlazorComponents()
    {
        using var fx = FixtureSolution.CreateBlazorApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var project = load.Solution.Projects.Single();

        var docs = new List<HarvestedDocument>();
        await foreach (var d in DocumentEnumerator.EnumerateAsync(project, default))
            docs.Add(d);

        var componentsGenerated = docs.Count(d => d.IsGenerated &&
            d.GeneratedPath.Contains("razor", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(fx.RazorComponentCount, componentsGenerated);
    }

    [Fact]
    public async Task EnumerateAsync_ReturnsMoreDocumentsThanProjectDocuments()
    {
        // The regression guard: project.Documents is on-disk only. If someone
        // "simplifies" the enumerator back to it, this fails.
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var project = load.Solution.Projects.Single();

        var total = 0;
        await foreach (var _ in DocumentEnumerator.EnumerateAsync(project, default)) total++;

        Assert.True(total > project.Documents.Count(),
            $"expected generated documents beyond the {project.Documents.Count()} on disk");
    }
}
