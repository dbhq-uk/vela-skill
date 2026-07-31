using System.Diagnostics;
using System.Text;

namespace Vela.Tests.Fixtures;

/// <summary>
/// A scaffolded solution in a temp directory of its own, for one test.
///
/// Every fixture a test is handed is still private to that test: several of them
/// deliberately corrupt a .csproj, corrupt a .sln, drop a vela.json beside the solution,
/// or edit a source file to make a project stale, so a shared directory would make those
/// tests depend on the order they ran in. What is shared is the SCAFFOLDING, which is
/// identical every time and is the expensive part: `dotnet new` and a restore cost several
/// seconds each, and this assembly asked for the same Razor Pages app around thirty times.
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

    /// <summary>
    /// A three-project graph: Lib, App which references it, and Leaf which references
    /// nothing.
    ///
    /// It exists because the incremental rebuild's one dangerous property is the CLOSURE,
    /// and a closure cannot be tested on a solution with one project in it. Two is not
    /// enough either: an edit to the upstream of a two-project solution selects both, and
    /// selecting every project is a full rebuild by another name, so the interesting case -
    /// some rebuilt, some reused - needs a third project that the change cannot reach.
    ///
    /// Written out as files and restored rather than scaffolded through `dotnet new`
    /// three times, which costs several seconds per project and produces more code than
    /// any of these tests reads.
    /// </summary>
    public static FixtureSolution CreateProjectGraph() => FromTemplate(Template.ProjectGraph);

    /// <summary>
    /// Three projects, two of which compile the SAME file: Alpha and Beta both include
    /// Shared/Shared.cs, and Gamma includes nothing of anybody's.
    ///
    /// One document in the index holds the occurrences of every project that compiles it,
    /// because documents are keyed by the file a developer can open. So replacing that
    /// document on behalf of one project deletes the other project's rows, and nothing
    /// puts them back. The file is compiled under a different symbol in each project, so
    /// the loss is visible rather than merely theoretical.
    /// </summary>
    /// <param name="betaCompilesShared">
    /// False leaves Beta compiling only its own file, so a test can ADD the shared file to
    /// Beta and watch what happens when a project starts compiling something another
    /// project was already compiling. The ledger cannot know about that pairing until
    /// after the rebuild that creates it, which is the one case a closure over the ledger
    /// alone cannot see.
    ///
    /// This is one template and not two. The two shapes differ by a single item in one
    /// project file, and a <c>&lt;Compile&gt;</c> item is not an input to restore: the
    /// assets file the copy carries describes packages and project references, neither of
    /// which moves. So the cheaper shape is the shared one restored once, with Beta's
    /// project file rewritten in the private copy afterwards - which is precisely the edit
    /// the test that asks for this then reverses, so nothing here is doing anything the
    /// suite does not already rely on working.
    /// </param>
    public static FixtureSolution CreateSharedFileSolution(bool betaCompilesShared = true)
    {
        var fx = FromTemplate(Template.SharedFile);
        if (!betaCompilesShared)
            WriteProject(fx.Root, "Beta");

        return fx;
    }

    /// <summary>
    /// Three projects that share no code at all, but which every one of them declares the
    /// same root-level `stylecop.json` as an `AdditionalFiles` item.
    ///
    /// That is the ordinary way to configure an analyser across a solution, and Roslyn
    /// hands the file to every project as an additional document. It becomes no document
    /// in the index - only a .cshtml or a .razor does - so it must not join these three
    /// projects into one shared-document group. If it does, an edit to any one of them
    /// closes over all three and `--incremental` is worth nothing on such a repository.
    /// </summary>
    public static FixtureSolution CreateSharedAnalyserFileSolution() =>
        FromTemplate(Template.SharedAnalyserFile);

    /// <summary>One class library, for the tests that only need an index to exist.</summary>
    public static FixtureSolution CreateLibrary() => FromTemplate(Template.Library);

    private static FixtureSolution FromTemplate(Template template)
    {
        var root = template.CopyToFreshRoot();
        return new FixtureSolution(root, Path.Combine(root, "Fixture.sln"));
    }

    /// <summary>Overwrites a file under the fixture, which is what an edit is.</summary>
    public void Write(string relativePath, string text) => WriteFile(Root, relativePath, text);

    public string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

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

        public static readonly Template ProjectGraph = new(root =>
        {
            WriteProject(root, "Lib");
            WriteFile(root, "Lib/Upstream.cs", """
                namespace Lib
                {
                    public static class Upstream
                    {
                        public static long Twice(int value) => value + value;
                    }
                }
                """);

            WriteProject(root, "App", references: new[] { "../Lib/Lib.csproj" });
            WriteFile(root, "App/Caller.cs", """
                namespace App
                {
                    public static class Caller
                    {
                        public static long Call() => Lib.Upstream.Twice(21);
                    }
                }
                """);

            WriteProject(root, "Leaf");
            WriteFile(root, "Leaf/Standalone.cs", """
                namespace Leaf
                {
                    public static class Standalone
                    {
                        public static int OriginalOnly() => 1;
                    }
                }
                """);

            Restore(root, "Lib/Lib.csproj", "App/App.csproj", "Leaf/Leaf.csproj");
        });

        public static readonly Template SharedFile = new(root =>
        {
            WriteFile(root, "Shared/Shared.cs", """
                namespace Shared
                {
                    public static class Common
                    {
                #if ALPHA
                        public static int FromAlpha() => 1;
                #else
                        public static int FromBeta() => 2;
                #endif
                    }
                }
                """);

            WriteProject(root, "Alpha", define: "ALPHA", compiles: "../Shared/Shared.cs");
            WriteFile(root, "Alpha/Own.cs", """
                namespace Alpha
                {
                    public static class Own
                    {
                        public static int Value() => 1;
                    }
                }
                """);

            WriteProject(root, "Beta", compiles: "../Shared/Shared.cs");
            WriteFile(root, "Beta/Own.cs", """
                namespace Beta
                {
                    public static class Own
                    {
                        public static int Value() => 2;
                    }
                }
                """);

            WriteProject(root, "Gamma");
            WriteFile(root, "Gamma/Own.cs", """
                namespace Gamma
                {
                    public static class Own
                    {
                        public static int Value() => 3;
                    }
                }
                """);

            Restore(root, "Alpha/Alpha.csproj", "Beta/Beta.csproj", "Gamma/Gamma.csproj");
        });

        public static readonly Template SharedAnalyserFile = new(root =>
        {
            WriteFile(root, "stylecop.json", """
                { "settings": { "documentationRules": { "companyName": "Fixture" } } }
                """);

            foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
            {
                WriteProject(root, name, additionalFiles: "../stylecop.json");
                WriteFile(root, $"{name}/Own.cs", $$"""
                    namespace {{name}}
                    {
                        public static class Own
                        {
                            public static int Value() => 1;
                        }
                    }
                    """);
            }

            Restore(root, "Alpha/Alpha.csproj", "Beta/Beta.csproj", "Gamma/Gamma.csproj");
        });

        public static readonly Template Library = new(root =>
        {
            WriteProject(root, "Solo");
            WriteFile(root, "Solo/Thing.cs", """
                namespace Solo
                {
                    public static class Thing
                    {
                        public static int Value() => 1;
                    }
                }
                """);

            Restore(root, "Solo/Solo.csproj");
        });

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

    /// <summary>
    /// A plain class library targeting the framework vela itself targets, with implicit
    /// usings and nullable annotations off so the generated code these tests reason about
    /// is exactly the code they wrote.
    /// </summary>
    private static void WriteProject(
        string root, string name, string[]? references = null, string? define = null, string? compiles = null,
        string? additionalFiles = null)
    {
        var items = new StringBuilder();
        if (compiles is not null)
            items.AppendLine($"""  <ItemGroup><Compile Include="{compiles}" Link="Shared.cs" /></ItemGroup>""");

        if (additionalFiles is not null)
            items.AppendLine($"""  <ItemGroup><AdditionalFiles Include="{additionalFiles}" /></ItemGroup>""");

        if (references is not null)
        {
            foreach (var reference in references)
                items.AppendLine($"""  <ItemGroup><ProjectReference Include="{reference}" /></ItemGroup>""");
        }

        WriteFile(root, $"{name}/{name}.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
                <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                {(define is null ? "" : $"<DefineConstants>$(DefineConstants);{define}</DefineConstants>")}
              </PropertyGroup>
            {items}</Project>
            """);
    }

    private static void WriteFile(string root, string relativePath, string text)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static void Restore(string root, params string[] projects)
    {
        Run("dotnet", "new sln -n Fixture --format sln", root);
        Run("dotnet", "sln Fixture.sln add " + string.Join(' ', projects), root);
        Run("dotnet", "restore Fixture.sln", root);
    }

    private static void Run(string exe, string args, string cwd, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // MSBuild keeps its worker nodes alive for fifteen minutes after a build so the
        // next build can reuse them. Those nodes are grandchildren of this process and
        // they INHERIT the two pipes below, so the pipes do not reach end-of-file when
        // `dotnet` exits - they reach it when the last node times out. A restore of a
        // multi-project solution spawns nodes; a restore of a single-project one does
        // not, which is why this stayed invisible until the fixtures grew a second
        // project. On CI it turned two-second restores into fifteen-minute ones, one
        // after another, and a suite that runs in ninety seconds took three hours.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

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

        // Give the async output handlers a moment to flush. Bounded on purpose: the
        // parameterless overload waits for the redirected streams to reach end-of-file
        // rather than for the process, so anything that inherited them can hold this
        // thread for as long as it likes. The environment variable above removes the one
        // thing that did; this is the belt to that brace, and it costs at most five
        // seconds of a truncated message on a command that already failed.
        p.WaitForExit(5_000);

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {args} failed: {stderrBuilder}{stdoutBuilder}");
    }
}
