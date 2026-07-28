using System.Diagnostics;
using System.Text;

namespace Vela.Tests.Fixtures;

public sealed class FixtureSolution : IDisposable
{
    public string Root { get; }
    public string SolutionPath { get; }
    public int RazorFileCount { get; private set; }
    public int RazorComponentCount { get; private set; }

    private FixtureSolution(string root, string solutionPath)
    {
        Root = root;
        SolutionPath = solutionPath;
    }

    /// <summary>A Razor Pages web app, scaffolded and restored, in a temp directory.</summary>
    public static FixtureSolution CreateWebApp()
    {
        var root = Path.Combine(Path.GetTempPath(), "vela-fx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Run("dotnet", "new webapp -o App --force", root);
        Run("dotnet", "new sln -n Fixture --format sln", root);
        Run("dotnet", "sln Fixture.sln add App/App.csproj", root);
        Run("dotnet", "restore Fixture.sln", root);

        var fx = new FixtureSolution(root, Path.Combine(root, "Fixture.sln"));
        fx.RazorFileCount = Directory
            .GetFiles(Path.Combine(root, "App"), "*.cshtml", SearchOption.AllDirectories)
            .Length;
        return fx;
    }

    /// <summary>A Blazor app, whose .razor components also reach the compilation
    /// through the Razor source generator.</summary>
    public static FixtureSolution CreateBlazorApp()
    {
        var root = Path.Combine(Path.GetTempPath(), "vela-fx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Run("dotnet", "new blazor -o App --force", root);
        Run("dotnet", "new sln -n Fixture --format sln", root);
        Run("dotnet", "sln Fixture.sln add App/App.csproj", root);
        Run("dotnet", "restore Fixture.sln", root);

        var fx = new FixtureSolution(root, Path.Combine(root, "Fixture.sln"));
        fx.RazorComponentCount = Directory
            .GetFiles(Path.Combine(root, "App"), "*.razor", SearchOption.AllDirectories)
            .Length;
        return fx;
    }

    private static void Run(string exe, string args, string cwd, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var p = Process.Start(psi)!;

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"{exe} {args} did not complete within {timeoutMs}ms and was killed.");
        }

        // Ensure async output handlers have finished flushing.
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {args} failed: {stderrBuilder}{stdoutBuilder}");
    }

    /// <summary>An empty solution file with no projects added, in a temp directory.</summary>
    public static FixtureSolution CreateEmptySolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "vela-fx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Run("dotnet", "new sln -n Empty --format sln", root);

        return new FixtureSolution(root, Path.Combine(root, "Empty.sln"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp dir, best effort */ }
    }
}
