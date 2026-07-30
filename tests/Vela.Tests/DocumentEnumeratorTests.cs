using Vela.Harvest;
using Vela.Tests.Fixtures;
using Xunit;

// The two Razor Pages tests here only read the fixture, so they share one harvest of
// it. The Blazor test needs a different scaffold and is the only test that wants one.
public class DocumentEnumeratorTests : IClassFixture<HarvestedWebApp>
{
    private readonly HarvestedWebApp _webApp;

    public DocumentEnumeratorTests(HarvestedWebApp webApp) => _webApp = webApp;

    [Fact]
    public async Task EnumerateAsync_IncludesOneGeneratedDocumentPerCshtml()
    {
        var project = _webApp.Project;

        var docs = new List<HarvestedDocument>();
        await foreach (var d in DocumentEnumerator.EnumerateAsync(project, default))
            docs.Add(d);

        var razorGenerated = docs.Count(d => d.IsGenerated &&
            d.GeneratedPath.Contains("cshtml", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(_webApp.RazorFileCount, razorGenerated);
        Assert.True(_webApp.RazorFileCount > 0, "fixture must contain .cshtml files");
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
        Assert.True(fx.RazorComponentCount > 0, "fixture must contain .razor components");
    }

    [Fact]
    public async Task EnumerateAsync_ReturnsMoreDocumentsThanProjectDocuments()
    {
        // The regression guard: project.Documents is on-disk only. If someone
        // "simplifies" the enumerator back to it, this fails.
        var project = _webApp.Project;

        var total = 0;
        await foreach (var _ in DocumentEnumerator.EnumerateAsync(project, default)) total++;

        Assert.True(total > project.Documents.Count(),
            $"expected generated documents beyond the {project.Documents.Count()} on disk");
    }
}
