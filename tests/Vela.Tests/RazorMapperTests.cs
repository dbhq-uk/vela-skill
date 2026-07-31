using Microsoft.CodeAnalysis;
using Vela.Harvest;
using Vela.Tests.Fixtures;
using Xunit;

// Both tests only read the fixture, so they share one harvest of it.
public class RazorMapperTests : IClassFixture<HarvestedWebApp>
{
    private readonly HarvestedWebApp _webApp;

    public RazorMapperTests(HarvestedWebApp webApp) => _webApp = webApp;

    [Fact]
    public async Task MapToOriginal_OnGeneratedRazorDocument_ReturnsTheCshtmlPath()
    {
        var project = _webApp.Project;

        HarvestedDocument? indexPage = null;
        await foreach (var d in DocumentEnumerator.EnumerateAsync(project, default))
            if (d.IsGenerated && d.GeneratedPath.Contains("Index", StringComparison.OrdinalIgnoreCase)
                              && d.GeneratedPath.Contains("cshtml", StringComparison.OrdinalIgnoreCase))
                indexPage = d;

        Assert.NotNull(indexPage);

        // Find any position that carries a #line mapping back to source.
        var root = await indexPage!.Tree.GetRootAsync();
        SourceLocation? mapped = null;
        foreach (var node in root.DescendantNodes())
        {
            mapped = RazorMapper.MapToOriginal(indexPage.Tree, node.SpanStart);
            if (mapped is not null && mapped.FilePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                break;
            mapped = null;
        }

        Assert.NotNull(mapped);
        Assert.EndsWith(".cshtml", mapped!.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(mapped.Line >= 0);
    }

    [Fact]
    public async Task MapToOriginal_OnOrdinaryCSharp_ReturnsTheFileItself()
    {
        var project = _webApp.Project;
        var doc = project.Documents.First(d => d.FilePath!.EndsWith(".cs"));
        var tree = (await doc.GetSyntaxTreeAsync())!;

        var mapped = RazorMapper.MapToOriginal(tree, 0);

        Assert.NotNull(mapped);
        Assert.EndsWith(".cs", mapped!.FilePath, StringComparison.OrdinalIgnoreCase);
    }
}
