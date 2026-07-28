using Vela.Harvest;
using Vela.Tests.Fixtures;
using Xunit;

public class WorkspaceLoaderTests
{
    [Fact]
    public async Task LoadAsync_OnValidSolution_ReturnsProjectsAndNoFailures()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var result = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        Assert.Empty(result.Failures);
        Assert.NotEmpty(result.Solution.Projects);
    }

    [Fact]
    public async Task LoadAsync_OnBrokenProject_ReportsFailureRatherThanReturningEmpty()
    {
        using var fx = FixtureSolution.CreateWebApp();
        // Corrupt the project so MSBuild cannot evaluate it.
        var csproj = Path.Combine(fx.Root, "App", "App.csproj");
        File.WriteAllText(csproj, "<Project><Unclosed></Project>");

        var result = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        // Constraint 4: the failure must be visible, not swallowed into an empty result.
        Assert.NotEmpty(result.Failures);
    }
}
