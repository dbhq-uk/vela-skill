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

        // Constraint 3: the failure must be visible, not swallowed into an empty result.
        Assert.NotEmpty(result.Failures);
    }

    [Fact]
    public async Task LoadAsync_OnUnparseableSolutionFile_ReportsFailureAndReturnsSafeSolution()
    {
        using var fx = FixtureSolution.CreateWebApp();
        // Corrupt the solution file itself so OpenSolutionAsync throws rather than
        // reporting via the WorkspaceFailed event.
        File.WriteAllText(fx.SolutionPath, "this is not a valid solution file at all");

        var result = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        // Constraint 3: an incomplete index must never look like a complete one, and the
        // catch-all fallback must still hand back something a caller can safely enumerate.
        Assert.NotEmpty(result.Failures);
        Assert.NotNull(result.Solution);
        Assert.Empty(result.Solution.Projects);
    }

    [Fact]
    public async Task LoadAsync_OnSolutionWithNoProjects_ReportsFailureRatherThanSilentEmptySuccess()
    {
        using var fx = FixtureSolution.CreateEmptySolution();

        var result = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        // Constraint 3: a solution that loads cleanly but contains zero projects is exactly
        // the silent-empty-success case that must never be mistaken for "no references exist".
        Assert.NotEmpty(result.Failures);
    }
}
