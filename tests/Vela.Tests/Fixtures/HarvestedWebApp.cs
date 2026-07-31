using Microsoft.CodeAnalysis;
using Vela.Harvest;
using Xunit;

namespace Vela.Tests.Fixtures;

/// <summary>
/// The Razor Pages fixture, loaded through Roslyn and emitted, once for a whole test
/// class rather than once per test.
///
/// It is shared by the tests that only READ it, and by no others. Loading a solution
/// runs a real design-time build and emitting runs the Razor source generator over every
/// view, which is seconds of work that a dozen tests were each paying for separately to
/// arrive at the identical answer. Nothing here is mutated afterwards: a Roslyn
/// <see cref="Microsoft.CodeAnalysis.Solution"/> is immutable, and no test writes to the
/// emitted index or to the fixture directory.
///
/// A test that corrupts its .csproj, corrupts its .sln, writes a vela.json beside the
/// solution, or otherwise changes what it was given must NOT use this. Those keep
/// calling <see cref="FixtureSolution.CreateWebApp"/> and get a directory of their own.
/// </summary>
public sealed class HarvestedWebApp : IAsyncLifetime
{
    private FixtureSolution? _fixture;

    /// <summary>The scaffolded solution on disk.</summary>
    public FixtureSolution Fixture => _fixture!;

    /// <summary>What <see cref="WorkspaceLoader.LoadAsync"/> said about it.</summary>
    public LoadResult Load { get; private set; } = null!;

    /// <summary>What <see cref="ScipEmitter.EmitAsync"/> made of it.</summary>
    public EmitResult Emitted { get; private set; } = null!;

    public string Root => Fixture.Root;
    public string SolutionPath => Fixture.SolutionPath;
    public int RazorFileCount => Fixture.RazorFileCount;

    /// <summary>The single project the fixture solution contains.</summary>
    public Project Project => Load.Solution.Projects.Single();

    public async Task InitializeAsync()
    {
        _fixture = FixtureSolution.CreateWebApp();
        Load = await WorkspaceLoader.LoadAsync(_fixture.SolutionPath, default);
        Emitted = await ScipEmitter.EmitAsync(Load.Solution, Load.Failures, default);
    }

    public Task DisposeAsync()
    {
        _fixture?.Dispose();
        return Task.CompletedTask;
    }
}
