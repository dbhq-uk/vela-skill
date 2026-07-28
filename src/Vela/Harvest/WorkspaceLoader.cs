using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Vela.Harvest;

public record LoadResult(Solution Solution, IReadOnlyList<string> Failures);

public static class WorkspaceLoader
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static async Task<LoadResult> LoadAsync(string solutionPath, CancellationToken ct)
    {
        EnsureMSBuildRegistered();

        var failures = new ConcurrentQueue<string>();
        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                failures.Enqueue(e.Diagnostic.Message);
        };

        Solution solution;
        try
        {
            solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Constraint 3: an incomplete index must never look like a complete one.
            // If OpenSolutionAsync throws rather than reporting via WorkspaceFailed, the
            // failure must still be visible in the result, not swallowed.
            failures.Enqueue(ex.Message);
            solution = workspace.CurrentSolution;
        }

        if (failures.IsEmpty && !solution.Projects.Any())
        {
            // Constraint 3: a solution that loads cleanly but contains zero projects looks
            // identical to "no references exist for the symbols in it". Record it as a
            // failure so no caller mistakes an empty index for a clean empty result.
            failures.Enqueue($"Solution '{solutionPath}' loaded but contained no projects.");
        }

        return new LoadResult(solution, failures.ToArray());
    }

    /// <summary>
    /// MSBuildLocator must run once per process and before any MSBuild type loads.
    /// </summary>
    private static void EnsureMSBuildRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
            _registered = true;
        }
    }
}
