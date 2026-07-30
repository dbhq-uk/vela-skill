using System.Diagnostics;
using System.Text;

namespace Vela.Tests.Fixtures;

/// <summary>
/// A scaffolded solution in a temp directory of its own, for one test.
///
/// Every fixture a test is handed is still private to that test: several of them
/// deliberately corrupt a .csproj, corrupt a .sln, or drop a vela.json beside the
/// solution, so a shared directory would make those tests depend on the order they ran
/// in. What is shared is the SCAFFOLDING, which is identical every time and is the
/// expensive part: `dotnet new` and a restore cost several seconds each, and this
/// assembly asked for the same Razor Pages app around thirty times.
///
/// So each shape is scaffolded and restored once per test run, into a template
/// directory, and every fixture after that is a directory copy of it. The copy is a few
/// megabytes of files and takes milliseconds. See <see cref="Template"/>.
/// </summary>
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
        var root = Template.WebApp.CopyToFreshRoot();

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
        var root = Template.BlazorApp.CopyToFreshRoot();

        var fx = new FixtureSolution(root, Path.Combine(root, "Fixture.sln"));
        fx.RazorComponentCount = Directory
            .GetFiles(Path.Combine(root, "App"), "*.razor", SearchOption.AllDirectories)
            .Length;
        return fx;
    }

    /// <summary>An empty solution file with no projects added, in a temp directory.</summary>
    public static FixtureSolution CreateEmptySolution()
    {
        var root = Template.EmptySolution.CopyToFreshRoot();
        return new FixtureSolution(root, Path.Combine(root, "Empty.sln"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp dir, best effort */ }
    }

    /// <summary>
    /// One scaffolded shape, built at most once per test run and copied thereafter.
    ///
    /// <see cref="Lazy{T}"/> with the default thread-safety mode is what makes "at most
    /// once" true across the parallel test collections: the first caller runs the
    /// scaffolding and every other caller waits for it.
    /// </summary>
    private sealed class Template
    {
        public static readonly Template WebApp = new(root =>
        {
            Run("dotnet", "new webapp -o App --force", root);
            Run("dotnet", "new sln -n Fixture --format sln", root);
            Run("dotnet", "sln Fixture.sln add App/App.csproj", root);
            Run("dotnet", "restore Fixture.sln", root);
        });

        public static readonly Template BlazorApp = new(root =>
        {
            Run("dotnet", "new blazor -o App --force", root);
            Run("dotnet", "new sln -n Fixture --format sln", root);
            Run("dotnet", "sln Fixture.sln add App/App.csproj", root);
            Run("dotnet", "restore Fixture.sln", root);
        });

        public static readonly Template EmptySolution = new(root =>
            Run("dotnet", "new sln -n Empty --format sln", root));

        private readonly Lazy<string> _path;

        private Template(Action<string> scaffold) => _path = new Lazy<string>(() =>
        {
            var path = Path.Combine(TemplateRoot.Value, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(path);
            scaffold(path);
            return path;
        });

        /// <summary>A private copy of this template, in a directory of its own.</summary>
        public string CopyToFreshRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "vela-fx-" + Guid.NewGuid().ToString("N")[..8]);
            CopyDirectory(_path.Value, root);
            return root;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));

            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        /// <summary>
        /// Where the templates live. They outlive every individual fixture, so they are
        /// removed when the test process exits rather than when a test finishes.
        /// </summary>
        private static readonly Lazy<string> TemplateRoot = new(() =>
        {
            var path = Path.Combine(
                Path.GetTempPath(), "vela-fx-template-" + Environment.ProcessId.ToString());
            Directory.CreateDirectory(path);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(path, recursive: true); } catch { /* temp dir, best effort */ }
            };
            return path;
        });
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
}
