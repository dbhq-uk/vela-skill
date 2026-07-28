# vela Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a .NET global tool that indexes a solution through Roslyn - including Razor and Blazor source-generated documents - into SQLite, and answers reference, caller and impact queries in milliseconds.

**Architecture:** Four replaceable layers: a Roslyn harvester walks the compilation (not the file system) and emits SCIP; a loader flattens SCIP into SQLite with FTS5; a CLI answers queries against that file; a skill teaches an agent when to reach for each verb. Nothing stays resident between queries.

**Tech Stack:** C# on .NET 8.0, Roslyn (`Microsoft.CodeAnalysis.*` 5.6.0), `Microsoft.Build.Locator` 1.11.2, `System.CommandLine` 2.0.10, `Microsoft.Data.Sqlite` 8.0.29, `Google.Protobuf` 3.35.1, xUnit.

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework: `net8.0`.** LTS and the widest reach. Do not raise it.
- **Deterministic only.** No model calls, no network calls at query or index time, no telemetry, no heuristic ranking. Every answer follows from Roslyn's semantic model.
- **Never write to the indexed repository.** The index is written to a cache directory outside the source tree. Indexing a repository must leave it byte-identical.
- **An incomplete index must never look like a complete one.** If any project fails to load, that fact is stored in the index and surfaced on every query that touches it, and the process exit code reflects it. Never return an empty result set that could be mistaken for "no references exist".
- **House style: British English, plain hyphens.** No em dashes, no en dashes, in code comments, output strings, or docs.
- **Tests are hermetic.** No network. Fixture solutions are built in temp directories and deleted after.
- **SCIP is the wire format.** Extend it via its own fields; do not fork the schema.

### Verified environment gotchas

These cost real time in the spike. Bake them in from Task 1 rather than rediscovering them.

1. **`error MSBL001`** - `Microsoft.Build.Locator` refuses to build when MSBuild assemblies arrive transitively without `ExcludeAssets="runtime" PrivateAssets="all"`. Task 1's csproj sets them.
2. **`error NU1605` (package downgrade)** - the pinned `Microsoft.Build*` versions must match what `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.6.0 pulls, which is **17.11.48**. Using 17.11.4 fails the build.
3. **`project.Documents` silently omits generated code.** Measured on a real solution: 146 on-disk documents versus 454 syntax trees, of which 307 were Razor. This single fact is the tool's reason to exist.
4. **The indexed solution must restore and build.** Roslyn loads the same MSBuild projects; unresolved packages produce load failures, which under Constraint 4 must be loud.

---

## File Structure

```
src/Vela/
  Vela.csproj                     packable global tool, ToolCommandName=vela
  Program.cs                      System.CommandLine wiring only
  Harvest/
    WorkspaceLoader.cs            MSBuildLocator + MSBuildWorkspace, load diagnostics
    DocumentEnumerator.cs         on-disk + source-generated documents
    RazorMapper.cs                generated position -> originating .cshtml/.razor
    ScipEmitter.cs                compilation -> SCIP messages
  Indexing/
    Schema.cs                     SQLite DDL
    ScipLoader.cs                 SCIP -> SQLite
    IndexPaths.cs                 cache-directory resolution
    IndexHealth.cs                degraded-state record and staleness
  Query/
    FindQuery.cs  DefQuery.cs  RefsQuery.cs  OutlineQuery.cs  ImpactQuery.cs
    OutputWriter.cs               context-window-shaped rendering
  Scip/scip.proto                 vendored from scip-code/scip

tests/Vela.Tests/
  Fixtures/FixtureSolution.cs     builds throwaway solutions in temp dirs
  WorkspaceLoaderTests.cs
  DocumentEnumeratorTests.cs      the coverage assertions that protect Razor
  RazorMapperTests.cs
  ScipEmitterTests.cs
  ScipLoaderTests.cs
  QueryTests.cs
  IndexHealthTests.cs

skills/vela/SKILL.md              already written
install.sh / install-codex.sh
```

Each query verb is its own file because they change independently and each is small; `OutputWriter` is shared so rendering stays consistent across them.

---

### Task 1: Packable tool skeleton

**Files:**
- Create: `src/Vela/Vela.csproj`, `src/Vela/Program.cs`
- Create: `tests/Vela.Tests/Vela.Tests.csproj`, `tests/Vela.Tests/SmokeTests.cs`
- Create: `Vela.sln`

**Interfaces:**
- Consumes: nothing
- Produces: a `vela` executable; `Program.BuildRootCommand()` returning `RootCommand` for tests to invoke without spawning a process

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Vela.Tests/SmokeTests.cs
using System.CommandLine;
using Xunit;

public class SmokeTests
{
    [Fact]
    public async Task RootCommand_WithNoArguments_ExitsNonZeroAndPrintsHelp()
    {
        var root = Program.BuildRootCommand();
        var exit = await root.InvokeAsync(Array.Empty<string>());
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public void RootCommand_HasTheFiveQueryVerbs()
    {
        var root = Program.BuildRootCommand();
        var names = root.Subcommands.Select(c => c.Name).ToHashSet();
        Assert.Contains("index", names);
        Assert.Contains("find", names);
        Assert.Contains("def", names);
        Assert.Contains("refs", names);
        Assert.Contains("outline", names);
        Assert.Contains("impact", names);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Vela.Tests -v q`
Expected: FAIL, `Program` does not exist.

- [ ] **Step 3: Write the csproj with the MSBuild pinning baked in**

```xml
<!-- src/Vela/Vela.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>vela</ToolCommandName>
    <PackageId>vela</PackageId>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Build.Locator" Version="1.11.2" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.6.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.6.0" />
    <PackageReference Include="System.CommandLine" Version="2.0.10" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.29" />
    <PackageReference Include="Google.Protobuf" Version="3.35.1" />

    <!-- MSBL001: MSBuildLocator requires these to be compile-time only, and
         NU1605: the versions must match what Workspaces.MSBuild 5.6.0 resolves. -->
    <PackageReference Include="Microsoft.Build" Version="17.11.48"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Build.Framework" Version="17.11.48"
                      ExcludeAssets="runtime" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Build.Utilities.Core" Version="17.11.48"
                      ExcludeAssets="runtime" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write Program.cs**

```csharp
// src/Vela/Program.cs
using System.CommandLine;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        BuildRootCommand().InvokeAsync(args);

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Compiler-exact code search for .NET.");
        foreach (var name in new[] { "index", "find", "def", "refs", "outline", "impact" })
            root.Add(new Command(name));
        return root;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests -v q`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Vela tests/Vela.Tests Vela.sln
git commit -m "feat: packable vela tool skeleton with MSBuild package pinning"
```

---

### Task 2: Workspace loader that fails loudly

**Files:**
- Create: `src/Vela/Harvest/WorkspaceLoader.cs`
- Create: `tests/Vela.Tests/Fixtures/FixtureSolution.cs`, `tests/Vela.Tests/WorkspaceLoaderTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 beyond the project reference
- Produces:
  - `record LoadResult(Solution Solution, IReadOnlyList<string> Failures)`
  - `Task<LoadResult> WorkspaceLoader.LoadAsync(string solutionPath, CancellationToken ct)`
  - `FixtureSolution.CreateWebApp()` returning `IDisposable` with `.SolutionPath` and `.RazorFileCount`

- [ ] **Step 1: Write the fixture helper**

```csharp
// tests/Vela.Tests/Fixtures/FixtureSolution.cs
using System.Diagnostics;

public sealed class FixtureSolution : IDisposable
{
    public string Root { get; }
    public string SolutionPath { get; }
    public int RazorFileCount { get; private set; }

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
        Run("dotnet", "new sln -n Fixture", root);
        Run("dotnet", "sln Fixture.sln add App/App.csproj", root);
        Run("dotnet", "restore Fixture.sln", root);

        var fx = new FixtureSolution(root, Path.Combine(root, "Fixture.sln"));
        fx.RazorFileCount = Directory
            .GetFiles(Path.Combine(root, "App"), "*.cshtml", SearchOption.AllDirectories)
            .Length;
        return fx;
    }

    private static void Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {args} failed: {p.StandardError.ReadToEnd()}");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp dir, best effort */ }
    }
}
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Vela.Tests/WorkspaceLoaderTests.cs
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
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Vela.Tests --filter WorkspaceLoaderTests -v q`
Expected: FAIL, `WorkspaceLoader` does not exist.

- [ ] **Step 4: Implement the loader**

```csharp
// src/Vela/Harvest/WorkspaceLoader.cs
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

        var failures = new List<string>();
        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                lock (failures) failures.Add(e.Diagnostic.Message);
        };

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        return new LoadResult(solution, failures);
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests --filter WorkspaceLoaderTests -v q`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Vela/Harvest/WorkspaceLoader.cs tests/Vela.Tests
git commit -m "feat: workspace loader that surfaces project load failures"
```

---

### Task 3: Generated-document coverage

This is the task the tool exists for. A regression here is silent: the index still builds, queries still answer, and the Razor half of a codebase disappears. The assertions are by count, deliberately.

**Files:**
- Create: `src/Vela/Harvest/DocumentEnumerator.cs`
- Create: `tests/Vela.Tests/DocumentEnumeratorTests.cs`
- Modify: `tests/Vela.Tests/Fixtures/FixtureSolution.cs` (add `CreateBlazorApp`)

**Interfaces:**
- Consumes: `LoadResult` from Task 2
- Produces:
  - `record HarvestedDocument(string GeneratedPath, SyntaxTree Tree, bool IsGenerated)`
  - `IAsyncEnumerable<HarvestedDocument> DocumentEnumerator.EnumerateAsync(Project project, CancellationToken ct)`

- [ ] **Step 1: Add the Blazor fixture**

```csharp
// tests/Vela.Tests/Fixtures/FixtureSolution.cs - add this method
public int RazorComponentCount { get; private set; }

/// <summary>A Blazor app, whose .razor components also reach the compilation
/// through the Razor source generator.</summary>
public static FixtureSolution CreateBlazorApp()
{
    var root = Path.Combine(Path.GetTempPath(), "vela-fx-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    Run("dotnet", "new blazor -o App --force", root);
    Run("dotnet", "new sln -n Fixture", root);
    Run("dotnet", "sln Fixture.sln add App/App.csproj", root);
    Run("dotnet", "restore Fixture.sln", root);

    var fx = new FixtureSolution(root, Path.Combine(root, "Fixture.sln"));
    fx.RazorComponentCount = Directory
        .GetFiles(Path.Combine(root, "App"), "*.razor", SearchOption.AllDirectories)
        .Length;
    return fx;
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Vela.Tests/DocumentEnumeratorTests.cs
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
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Vela.Tests --filter DocumentEnumeratorTests -v q`
Expected: FAIL, `DocumentEnumerator` does not exist.

- [ ] **Step 4: Implement the enumerator**

```csharp
// src/Vela/Harvest/DocumentEnumerator.cs
using Microsoft.CodeAnalysis;

namespace Vela.Harvest;

public record HarvestedDocument(string GeneratedPath, SyntaxTree Tree, bool IsGenerated);

public static class DocumentEnumerator
{
    /// <summary>
    /// Yields every document Roslyn compiles, not every file on disk.
    ///
    /// Razor views and Blazor components never exist as files the compiler reads;
    /// the Razor source generator emits them into the compilation. Enumerating
    /// project.Documents therefore misses all of them, which is exactly the bug
    /// that makes every other .NET indexer Razor-blind.
    /// </summary>
    public static async IAsyncEnumerable<HarvestedDocument> EnumerateAsync(
        Project project,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var doc in project.Documents)
        {
            var tree = await doc.GetSyntaxTreeAsync(ct);
            if (tree is not null)
                yield return new HarvestedDocument(doc.FilePath ?? doc.Name, tree, IsGenerated: false);
        }

        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(ct))
        {
            var tree = await generated.GetSyntaxTreeAsync(ct);
            if (tree is not null)
                yield return new HarvestedDocument(generated.HintName, tree, IsGenerated: true);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests --filter DocumentEnumeratorTests -v q`
Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Vela/Harvest/DocumentEnumerator.cs tests/Vela.Tests
git commit -m "feat: enumerate source-generated documents so Razor and Blazor are indexed"
```

---

### Task 4: Map generated positions back to the view file

A hit reported against `Pages_Index_cshtml.g.cs` is useless. It must be reported against `Pages/Index.cshtml` at the line the developer can open.

**Files:**
- Create: `src/Vela/Harvest/RazorMapper.cs`
- Create: `tests/Vela.Tests/RazorMapperTests.cs`

**Interfaces:**
- Consumes: `HarvestedDocument` from Task 3
- Produces:
  - `record SourceLocation(string FilePath, int Line, int Character)`
  - `SourceLocation? RazorMapper.MapToOriginal(SyntaxTree tree, int position)`

Roslyn already parses `#line` directives, so this uses `GetMappedLineSpan` rather than hand-parsing. Verified in the spike: generated Razor carries both `#pragma checksum "<abs path to .cshtml>"` and `#line (l,c)-(l,c) "<path>"`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Vela.Tests/RazorMapperTests.cs
using Microsoft.CodeAnalysis;
using Xunit;

public class RazorMapperTests
{
    [Fact]
    public async Task MapToOriginal_OnGeneratedRazorDocument_ReturnsTheCshtmlPath()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var project = load.Solution.Projects.Single();

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
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var project = load.Solution.Projects.Single();
        var doc = project.Documents.First(d => d.FilePath!.EndsWith(".cs"));
        var tree = (await doc.GetSyntaxTreeAsync())!;

        var mapped = RazorMapper.MapToOriginal(tree, 0);

        Assert.NotNull(mapped);
        Assert.EndsWith(".cs", mapped!.FilePath, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Vela.Tests --filter RazorMapperTests -v q`
Expected: FAIL, `RazorMapper` does not exist.

- [ ] **Step 3: Implement the mapper**

```csharp
// src/Vela/Harvest/RazorMapper.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vela.Harvest;

public record SourceLocation(string FilePath, int Line, int Character);

public static class RazorMapper
{
    /// <summary>
    /// Resolves a position in a syntax tree to the file a developer can open.
    ///
    /// For generated Razor, the tree carries #line directives pointing back at the
    /// originating .cshtml or .razor, and Roslyn resolves them via GetMappedLineSpan.
    /// For ordinary C# the mapped span is the file itself.
    /// </summary>
    public static SourceLocation? MapToOriginal(SyntaxTree tree, int position)
    {
        if (position < 0 || position > tree.Length) return null;

        var mapped = tree.GetMappedLineSpan(new TextSpan(position, 0));
        var path = string.IsNullOrEmpty(mapped.Path) ? tree.FilePath : mapped.Path;
        if (string.IsNullOrEmpty(path)) return null;

        return new SourceLocation(path, mapped.StartLinePosition.Line, mapped.StartLinePosition.Character);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests --filter RazorMapperTests -v q`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Vela/Harvest/RazorMapper.cs tests/Vela.Tests/RazorMapperTests.cs
git commit -m "feat: map generated Razor positions back to the originating view"
```

---

### Task 5: Emit SCIP

**Files:**
- Create: `src/Vela/Scip/scip.proto` (vendored from `scip-code/scip`)
- Create: `src/Vela/Harvest/ScipEmitter.cs`
- Create: `tests/Vela.Tests/ScipEmitterTests.cs`
- Modify: `src/Vela/Vela.csproj` (add `<Protobuf Include="Scip/scip.proto" />` and `Grpc.Tools`)

**Interfaces:**
- Consumes: `HarvestedDocument` (Task 3), `RazorMapper.MapToOriginal` (Task 4), `LoadResult` (Task 2)
- Produces: `Task<Scip.Index> ScipEmitter.EmitAsync(Solution solution, IReadOnlyList<string> failures, CancellationToken ct)`

Emit `enclosing_range` on definition occurrences. That field is what turns "who references this" into "who calls this" without interval arithmetic at query time, and it is the second thing `scip-dotnet` omits.

- [ ] **Step 1: Vendor the proto and wire codegen**

```bash
curl -sL https://raw.githubusercontent.com/scip-code/scip/main/scip.proto \
  -o src/Vela/Scip/scip.proto
```

Add to `src/Vela/Vela.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="Grpc.Tools" Version="2.71.0" PrivateAssets="all" />
    <Protobuf Include="Scip/scip.proto" GrpcServices="None" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Vela.Tests/ScipEmitterTests.cs
using Xunit;

public class ScipEmitterTests
{
    [Fact]
    public async Task EmitAsync_ProducesADocumentForEveryRazorView()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        var razorDocs = index.Documents.Count(d =>
            d.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(fx.RazorFileCount, razorDocs);
    }

    [Fact]
    public async Task EmitAsync_RecordsEnclosingRangeOnDefinitions()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        var definitionsWithEnclosure = index.Documents
            .SelectMany(d => d.Occurrences)
            .Count(o => o.EnclosingRange.Count > 0);

        Assert.True(definitionsWithEnclosure > 0,
            "enclosing_range is what makes callers a stored edge rather than an inference");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Vela.Tests --filter ScipEmitterTests -v q`
Expected: FAIL, `ScipEmitter` does not exist.

- [ ] **Step 4: Implement the emitter**

```csharp
// src/Vela/Harvest/ScipEmitter.cs
using Microsoft.CodeAnalysis;
using Vela.Harvest;

namespace Vela.Harvest;

public static class ScipEmitter
{
    public static async Task<Scip.Index> EmitAsync(
        Solution solution, IReadOnlyList<string> failures, CancellationToken ct)
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                Version = Scip.ProtocolVersion.UnspecifiedProtocolVersion,
                ToolInfo = new Scip.ToolInfo { Name = "vela", Version = ThisAssemblyVersion() },
                ProjectRoot = new Uri(Path.GetDirectoryName(solution.FilePath)!).AbsoluteUri
            }
        };

        // Documents are keyed by the file a developer can open, so every generated
        // Razor occurrence folds into its originating .cshtml or .razor document.
        var byOriginalPath = new Dictionary<string, Scip.Document>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            await foreach (var harvested in DocumentEnumerator.EnumerateAsync(project, ct))
            {
                var model = compilation.GetSemanticModel(harvested.Tree);
                var root = await harvested.Tree.GetRootAsync(ct);

                foreach (var node in root.DescendantNodes())
                {
                    var symbol = model.GetDeclaredSymbol(node, ct)
                                 ?? model.GetSymbolInfo(node, ct).Symbol;
                    if (symbol is null) continue;

                    var location = RazorMapper.MapToOriginal(harvested.Tree, node.SpanStart);
                    if (location is null) continue;

                    var doc = GetOrAddDocument(byOriginalPath, index, location.FilePath, solution);
                    var isDefinition = model.GetDeclaredSymbol(node, ct) is not null;

                    var occurrence = new Scip.Occurrence
                    {
                        Symbol = SymbolIdentity.For(symbol),
                        SymbolRoles = isDefinition ? (int)Scip.SymbolRole.Definition : 0
                    };
                    occurrence.Range.AddRange(new[] { location.Line, location.Character, location.Character });

                    if (isDefinition)
                    {
                        var enclosing = RazorMapper.MapToOriginal(harvested.Tree, node.Span.End);
                        if (enclosing is not null)
                            occurrence.EnclosingRange.AddRange(new[]
                            {
                                location.Line, location.Character, enclosing.Line, enclosing.Character
                            });
                    }

                    doc.Occurrences.Add(occurrence);
                }
            }
        }

        // Constraint 4: load failures travel with the index.
        foreach (var failure in failures)
            index.Metadata.ToolInfo.Arguments.Add("load-failure: " + failure);

        return index;
    }

    private static Scip.Document GetOrAddDocument(
        Dictionary<string, Scip.Document> map, Scip.Index index, string path, Solution solution)
    {
        if (map.TryGetValue(path, out var existing)) return existing;

        var root = Path.GetDirectoryName(solution.FilePath)!;
        var relative = Path.GetRelativePath(root, path);
        var doc = new Scip.Document { RelativePath = relative, Language = LanguageOf(path) };
        map[path] = doc;
        index.Documents.Add(doc);
        return doc;
    }

    private static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".vb" => "vb",
        ".cshtml" => "razor",
        ".razor" => "razor",
        _ => "unknown"
    };

    private static string ThisAssemblyVersion() =>
        typeof(ScipEmitter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>Stable, cross-project identity for a symbol.</summary>
public static class SymbolIdentity
{
    private static readonly SymbolDisplayFormat Format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType);

    public static string For(ISymbol symbol) => symbol.ToDisplayString(Format);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests --filter ScipEmitterTests -v q`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Vela/Scip src/Vela/Harvest/ScipEmitter.cs src/Vela/Vela.csproj tests/Vela.Tests/ScipEmitterTests.cs
git commit -m "feat: emit SCIP with Razor documents and enclosing ranges"
```

---

### Task 6: SQLite schema and loader

**Files:**
- Create: `src/Vela/Indexing/Schema.cs`, `src/Vela/Indexing/IndexPaths.cs`, `src/Vela/Indexing/ScipLoader.cs`
- Create: `tests/Vela.Tests/ScipLoaderTests.cs`

**Interfaces:**
- Consumes: `Scip.Index` from Task 5
- Produces:
  - `string IndexPaths.ForSolution(string solutionPath)` - cache path, honouring Constraint 3
  - `void Schema.Create(SqliteConnection db)`
  - `void ScipLoader.Load(SqliteConnection db, Scip.Index index)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Vela.Tests/ScipLoaderTests.cs
using Microsoft.Data.Sqlite;
using Xunit;

public class ScipLoaderTests
{
    [Fact]
    public async Task Load_PopulatesDocumentsAndOccurrences()
    {
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        Assert.Equal(index.Documents.Count, ScalarInt(db, "SELECT COUNT(*) FROM document"));
        Assert.True(ScalarInt(db, "SELECT COUNT(*) FROM occurrence") > 0);
    }

    [Fact]
    public void Schema_CreatesAnFts5SymbolIndex()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ('Perfume.Status')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_fts WHERE symbol_fts MATCH 'Status'";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void IndexPaths_ResolvesOutsideTheSolutionDirectory()
    {
        // Constraint 3: indexing must not write into the repository.
        using var fx = FixtureSolution.CreateWebApp();
        var path = IndexPaths.ForSolution(fx.SolutionPath);
        Assert.False(path.StartsWith(fx.Root, StringComparison.OrdinalIgnoreCase));
    }

    private static int ScalarInt(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Vela.Tests --filter ScipLoaderTests -v q`
Expected: FAIL, `Schema` does not exist.

- [ ] **Step 3: Implement schema, paths and loader**

```csharp
// src/Vela/Indexing/IndexPaths.cs
using System.Security.Cryptography;
using System.Text;

namespace Vela.Indexing;

public static class IndexPaths
{
    /// <summary>
    /// Indexes live in the user cache directory, keyed by solution path.
    /// Constraint 3: never write into the repository being indexed.
    /// </summary>
    public static string ForSolution(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(full);

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

        var dir = Path.Combine(cache, "vela");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{name}-{hash}.db");
    }
}
```

```csharp
// src/Vela/Indexing/Schema.cs
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public static class Schema
{
    public static void Create(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS document (
                id           INTEGER PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                language      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS occurrence (
                id            INTEGER PRIMARY KEY,
                document_id   INTEGER NOT NULL REFERENCES document(id),
                symbol        TEXT NOT NULL,
                is_definition INTEGER NOT NULL,
                start_line    INTEGER NOT NULL,
                start_char    INTEGER NOT NULL,
                enc_end_line  INTEGER,
                enc_end_char  INTEGER
            );

            CREATE INDEX IF NOT EXISTS ix_occurrence_symbol ON occurrence(symbol);
            CREATE INDEX IF NOT EXISTS ix_occurrence_document ON occurrence(document_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS symbol_fts USING fts5(symbol);

            -- Constraint 4: an index that could not be built completely says so.
            CREATE TABLE IF NOT EXISTS index_health (
                built_at_utc TEXT NOT NULL,
                git_ref      TEXT,
                degraded     INTEGER NOT NULL,
                detail       TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
```

```csharp
// src/Vela/Indexing/ScipLoader.cs
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public static class ScipLoader
{
    public static void Load(SqliteConnection db, Scip.Index index)
    {
        using var tx = db.BeginTransaction();

        using var insertDoc = db.CreateCommand();
        insertDoc.CommandText =
            "INSERT INTO document(relative_path, language) VALUES ($p, $l) RETURNING id";
        insertDoc.Parameters.Add("$p", SqliteType.Text);
        insertDoc.Parameters.Add("$l", SqliteType.Text);

        using var insertOcc = db.CreateCommand();
        insertOcc.CommandText = """
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char)
            VALUES ($d, $s, $def, $sl, $sc, $el, $ec)
            """;
        foreach (var name in new[] { "$d", "$s", "$def", "$sl", "$sc", "$el", "$ec" })
            insertOcc.Parameters.Add(name, SqliteType.Integer);
        insertOcc.Parameters["$s"].SqliteType = SqliteType.Text;

        using var insertFts = db.CreateCommand();
        insertFts.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ($s)";
        insertFts.Parameters.Add("$s", SqliteType.Text);

        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var doc in index.Documents)
        {
            insertDoc.Parameters["$p"].Value = doc.RelativePath;
            insertDoc.Parameters["$l"].Value = doc.Language;
            var docId = Convert.ToInt64(insertDoc.ExecuteScalar());

            foreach (var occ in doc.Occurrences)
            {
                var isDef = (occ.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0;
                insertOcc.Parameters["$d"].Value = docId;
                insertOcc.Parameters["$s"].Value = occ.Symbol;
                insertOcc.Parameters["$def"].Value = isDef ? 1 : 0;
                insertOcc.Parameters["$sl"].Value = occ.Range.Count > 0 ? occ.Range[0] : 0;
                insertOcc.Parameters["$sc"].Value = occ.Range.Count > 1 ? occ.Range[1] : 0;
                insertOcc.Parameters["$el"].Value =
                    occ.EnclosingRange.Count > 2 ? occ.EnclosingRange[2] : (object)DBNull.Value;
                insertOcc.Parameters["$ec"].Value =
                    occ.EnclosingRange.Count > 3 ? occ.EnclosingRange[3] : (object)DBNull.Value;
                insertOcc.ExecuteNonQuery();

                if (seenSymbols.Add(occ.Symbol))
                {
                    insertFts.Parameters["$s"].Value = occ.Symbol;
                    insertFts.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests --filter ScipLoaderTests -v q`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Vela/Indexing tests/Vela.Tests/ScipLoaderTests.cs
git commit -m "feat: SQLite schema, cache-directory paths and SCIP loader"
```

---

### Task 7: Index health and the honesty guarantee

Constraint 4 gets its own task because it is the constraint most easily lost, and because every query depends on it.

**Files:**
- Create: `src/Vela/Indexing/IndexHealth.cs`
- Create: `tests/Vela.Tests/IndexHealthTests.cs`

**Interfaces:**
- Consumes: `Schema` (Task 6)
- Produces:
  - `record HealthRecord(DateTime BuiltAtUtc, string? GitRef, bool Degraded, string? Detail)`
  - `void IndexHealth.Write(SqliteConnection db, HealthRecord record)`
  - `HealthRecord IndexHealth.Read(SqliteConnection db)`
  - `const int ExitDegraded = 3`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Vela.Tests/IndexHealthTests.cs
using Microsoft.Data.Sqlite;
using Xunit;

public class IndexHealthTests
{
    [Fact]
    public void Read_AfterWritingDegradedState_ReportsDegraded()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", Degraded: true, "App.csproj failed to load"));
        var health = IndexHealth.Read(db);

        Assert.True(health.Degraded);
        Assert.Contains("App.csproj", health.Detail);
    }

    [Fact]
    public void Read_OnAHealthyIndex_ReportsNotDegraded()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", Degraded: false, null));

        Assert.False(IndexHealth.Read(db).Degraded);
    }

    [Fact]
    public void ExitDegraded_IsDistinctFromSuccessAndFromUsageError()
    {
        // A degraded answer must be distinguishable by a caller, not just by a human.
        Assert.NotEqual(0, IndexHealth.ExitDegraded);
        Assert.NotEqual(1, IndexHealth.ExitDegraded);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Vela.Tests --filter IndexHealthTests -v q`
Expected: FAIL, `IndexHealth` does not exist.

- [ ] **Step 3: Implement index health**

```csharp
// src/Vela/Indexing/IndexHealth.cs
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

public record HealthRecord(DateTime BuiltAtUtc, string? GitRef, bool Degraded, string? Detail);

public static class IndexHealth
{
    /// <summary>Exit code for an answer produced from a degraded or stale index.</summary>
    public const int ExitDegraded = 3;

    public static void Write(SqliteConnection db, HealthRecord record)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM index_health;
            INSERT INTO index_health(built_at_utc, git_ref, degraded, detail)
            VALUES ($b, $g, $d, $t);
            """;
        cmd.Parameters.AddWithValue("$b", record.BuiltAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$g", (object?)record.GitRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", record.Degraded ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", (object?)record.Detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static HealthRecord Read(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT built_at_utc, git_ref, degraded, detail FROM index_health LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new HealthRecord(DateTime.MinValue, null, Degraded: true, "index has no health record");

        return new HealthRecord(
            DateTime.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests --filter IndexHealthTests -v q`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Vela/Indexing/IndexHealth.cs tests/Vela.Tests/IndexHealthTests.cs
git commit -m "feat: index health record so a degraded index can never look complete"
```

---

### Task 8: Query verbs

**Files:**
- Create: `src/Vela/Query/OutputWriter.cs`, `src/Vela/Query/FindQuery.cs`, `src/Vela/Query/DefQuery.cs`, `src/Vela/Query/RefsQuery.cs`, `src/Vela/Query/OutlineQuery.cs`, `src/Vela/Query/ImpactQuery.cs`
- Create: `tests/Vela.Tests/QueryTests.cs`
- Modify: `src/Vela/Program.cs` (wire verbs to handlers)

**Interfaces:**
- Consumes: `Schema`, `ScipLoader`, `IndexHealth`
- Produces:
  - `record Hit(string RelativePath, int Line, int Character, string Symbol, bool IsDefinition)`
  - `IReadOnlyList<Hit> RefsQuery.Run(SqliteConnection db, string symbolPattern)`
  - `IReadOnlyList<Hit> DefQuery.Run(SqliteConnection db, string symbolPattern)`
  - `IReadOnlyList<string> FindQuery.Run(SqliteConnection db, string pattern)`
  - `IReadOnlyList<Hit> OutlineQuery.Run(SqliteConnection db, string relativePath)`
  - `IReadOnlyList<Hit> ImpactQuery.Run(SqliteConnection db, string symbolPattern)`
  - `string OutputWriter.Render(IReadOnlyList<Hit> hits, HealthRecord health)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Vela.Tests/QueryTests.cs
using Microsoft.Data.Sqlite;
using Xunit;

public class QueryTests
{
    private static SqliteConnection SeededDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document(id, relative_path, language) VALUES
                (1, 'App/Models/Perfume.cs', 'csharp'),
                (2, 'App/Pages/Index.cshtml', 'razor');
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                (1, 'App.Models.Perfume.Status', 1, 10, 4, 12, 5),
                (2, 'App.Models.Perfume.Status', 0, 7, 12, NULL, NULL),
                (1, 'App.Models.Perfume.Name',   1, 20, 4, 22, 5);
            INSERT INTO symbol_fts(symbol) VALUES
                ('App.Models.Perfume.Status'), ('App.Models.Perfume.Name');
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", false, null));
        return db;
    }

    [Fact]
    public void Refs_ReturnsBothCSharpAndRazorOccurrences()
    {
        using var db = SeededDb();
        var hits = RefsQuery.Run(db, "Perfume.Status");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.RelativePath.EndsWith(".cshtml"));
    }

    [Fact]
    public void Def_ReturnsOnlyTheDefinition()
    {
        using var db = SeededDb();
        var hits = DefQuery.Run(db, "Perfume.Status");

        Assert.Single(hits);
        Assert.True(hits[0].IsDefinition);
        Assert.Equal(10, hits[0].Line);
    }

    [Fact]
    public void Outline_ReturnsDefinitionsInOneFile()
    {
        using var db = SeededDb();
        var hits = OutlineQuery.Run(db, "App/Models/Perfume.cs");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.IsDefinition));
    }

    [Fact]
    public void Find_MatchesPartialSymbolNames()
    {
        using var db = SeededDb();
        var symbols = FindQuery.Run(db, "Status");

        Assert.Contains("App.Models.Perfume.Status", symbols);
    }

    [Fact]
    public void Render_OnDegradedIndex_SaysSoInTheOutput()
    {
        using var db = SeededDb();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc", true, "App.csproj failed to load"));

        var output = OutputWriter.Render(RefsQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        Assert.Contains("INCOMPLETE", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("App.csproj", output);
    }

    [Fact]
    public void Render_GroupsHitsByFile()
    {
        using var db = SeededDb();
        var output = OutputWriter.Render(RefsQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        Assert.Contains("App/Pages/Index.cshtml", output);
        Assert.Contains("App/Models/Perfume.cs", output);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Vela.Tests --filter QueryTests -v q`
Expected: FAIL, query types do not exist.

- [ ] **Step 3: Implement the queries**

```csharp
// src/Vela/Query/Hit.cs
namespace Vela.Query;

public record Hit(string RelativePath, int Line, int Character, string Symbol, bool IsDefinition);
```

```csharp
// src/Vela/Query/RefsQuery.cs
using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class RefsQuery
{
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.Select(db, """
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE o.symbol LIKE '%' || $s
               OR o.symbol LIKE '%' || $s || '(%'
            ORDER BY d.relative_path, o.start_line
            """, symbolPattern);
}
```

```csharp
// src/Vela/Query/DefQuery.cs
using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class DefQuery
{
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.Select(db, """
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE o.is_definition = 1
              AND (o.symbol LIKE '%' || $s OR o.symbol LIKE '%' || $s || '(%')
            ORDER BY d.relative_path, o.start_line
            """, symbolPattern);
}
```

```csharp
// src/Vela/Query/OutlineQuery.cs
using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class OutlineQuery
{
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string relativePath)
        => QueryHelper.Select(db, """
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE d.relative_path = $s AND o.is_definition = 1
            ORDER BY o.start_line
            """, relativePath);
}
```

```csharp
// src/Vela/Query/ImpactQuery.cs
using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class ImpactQuery
{
    /// <summary>
    /// Callers, derived from stored enclosing ranges: a reference to the target
    /// that falls inside another symbol's enclosing range is a call from it.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string symbolPattern)
        => QueryHelper.Select(db, """
            SELECT d.relative_path, caller.start_line, caller.start_char, caller.symbol, 1
            FROM occurrence target
            JOIN document d ON d.id = target.document_id
            JOIN occurrence caller
              ON caller.document_id = target.document_id
             AND caller.is_definition = 1
             AND caller.enc_end_line IS NOT NULL
             AND target.start_line BETWEEN caller.start_line AND caller.enc_end_line
            WHERE target.is_definition = 0
              AND (target.symbol LIKE '%' || $s OR target.symbol LIKE '%' || $s || '(%')
            GROUP BY caller.symbol, d.relative_path, caller.start_line
            ORDER BY d.relative_path, caller.start_line
            """, symbolPattern);
}
```

```csharp
// src/Vela/Query/FindQuery.cs
using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class FindQuery
{
    public static IReadOnlyList<string> Run(SqliteConnection db, string pattern)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT symbol FROM symbol_fts WHERE symbol_fts MATCH $p ORDER BY symbol";
        cmd.Parameters.AddWithValue("$p", pattern);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }
}
```

```csharp
// src/Vela/Query/QueryHelper.cs
using Microsoft.Data.Sqlite;

namespace Vela.Query;

internal static class QueryHelper
{
    public static IReadOnlyList<Hit> Select(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", parameter);

        var hits = new List<Hit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            hits.Add(new Hit(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                             reader.GetString(3), reader.GetInt32(4) != 0));
        return hits;
    }
}
```

```csharp
// src/Vela/Query/OutputWriter.cs
using System.Text;
using Vela.Indexing;

namespace Vela.Query;

public static class OutputWriter
{
    /// <summary>
    /// Renders for a context window: grouped by file, one line per hit, and a
    /// loud banner when the index cannot be trusted to be complete.
    /// </summary>
    public static string Render(IReadOnlyList<Hit> hits, HealthRecord health)
    {
        var sb = new StringBuilder();

        if (health.Degraded)
        {
            sb.AppendLine("!! INCOMPLETE INDEX - these results may be missing references.");
            if (!string.IsNullOrEmpty(health.Detail)) sb.AppendLine("   " + health.Detail);
            sb.AppendLine("   Do not treat an empty or short result as proof the symbol is unused.");
            sb.AppendLine();
        }

        foreach (var group in hits.GroupBy(h => h.RelativePath).OrderBy(g => g.Key))
        {
            sb.AppendLine(group.Key);
            foreach (var hit in group.OrderBy(h => h.Line))
                sb.AppendLine($"  {hit.Line + 1,6}:{hit.Character + 1,-4} {(hit.IsDefinition ? "def" : "ref")}  {hit.Symbol}");
        }

        sb.AppendLine();
        sb.AppendLine($"{hits.Count} result(s)");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Vela.Tests --filter QueryTests -v q`
Expected: PASS, 6 tests.

- [ ] **Step 5: Wire the verbs into Program.cs**

```csharp
// src/Vela/Program.cs - replace BuildRootCommand
using System.CommandLine;
using Microsoft.Data.Sqlite;
using Vela.Indexing;
using Vela.Query;

public static class Program
{
    public static Task<int> Main(string[] args) => BuildRootCommand().InvokeAsync(args);

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Compiler-exact code search for .NET.");
        var solutionOption = new Option<string>("--solution", () => FindSolution(), "Path to the .sln");

        root.Add(BuildIndexCommand(solutionOption));
        root.Add(BuildQueryCommand("refs",    "Every usage of a symbol",       solutionOption, RefsQuery.Run));
        root.Add(BuildQueryCommand("def",     "Where a symbol is defined",     solutionOption, DefQuery.Run));
        root.Add(BuildQueryCommand("impact",  "Callers and blast radius",      solutionOption, ImpactQuery.Run));
        root.Add(BuildQueryCommand("outline", "Symbols defined in a file",     solutionOption, OutlineQuery.Run));
        root.Add(BuildFindCommand(solutionOption));
        return root;
    }

    private static Command BuildQueryCommand(
        string name, string description, Option<string> solutionOption,
        Func<SqliteConnection, string, IReadOnlyList<Hit>> run)
    {
        var arg = new Argument<string>("symbol");
        var cmd = new Command(name, description) { arg, solutionOption };
        cmd.SetHandler((string symbol, string solution) =>
        {
            using var db = OpenIndex(solution);
            var health = IndexHealth.Read(db);
            Console.Write(OutputWriter.Render(run(db, symbol), health));
            Environment.ExitCode = health.Degraded ? IndexHealth.ExitDegraded : 0;
        }, arg, solutionOption);
        return cmd;
    }

    private static Command BuildFindCommand(Option<string> solutionOption)
    {
        var arg = new Argument<string>("pattern");
        var cmd = new Command("find", "Symbol search by name") { arg, solutionOption };
        cmd.SetHandler((string pattern, string solution) =>
        {
            using var db = OpenIndex(solution);
            foreach (var symbol in FindQuery.Run(db, pattern)) Console.WriteLine(symbol);
        }, arg, solutionOption);
        return cmd;
    }

    private static Command BuildIndexCommand(Option<string> solutionOption)
    {
        var cmd = new Command("index", "Build the index for a solution") { solutionOption };
        cmd.SetHandler(async (string solution) =>
        {
            var load = await Vela.Harvest.WorkspaceLoader.LoadAsync(solution, default);
            var index = await Vela.Harvest.ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

            var path = IndexPaths.ForSolution(solution);
            if (File.Exists(path)) File.Delete(path);

            using var db = new SqliteConnection($"Data Source={path}");
            db.Open();
            Schema.Create(db);
            ScipLoader.Load(db, index);
            IndexHealth.Write(db, new HealthRecord(
                DateTime.UtcNow, null,
                Degraded: load.Failures.Count > 0,
                Detail: load.Failures.Count > 0 ? string.Join("; ", load.Failures) : null));

            Console.WriteLine($"Indexed {index.Documents.Count} documents to {path}");
            if (load.Failures.Count > 0)
            {
                Console.Error.WriteLine($"!! {load.Failures.Count} project(s) failed to load. The index is INCOMPLETE.");
                Environment.ExitCode = IndexHealth.ExitDegraded;
            }
        }, solutionOption);
        return cmd;
    }

    private static SqliteConnection OpenIndex(string solution)
    {
        var path = IndexPaths.ForSolution(solution);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No index for {solution}. Run: vela index");
            Environment.Exit(1);
        }
        var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        return db;
    }

    private static string FindSolution()
    {
        var found = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.sln");
        return found.Length == 1 ? found[0] : "";
    }
}
```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test -v q`
Expected: PASS, all tests.

- [ ] **Step 7: Commit**

```bash
git add src/Vela/Query src/Vela/Program.cs tests/Vela.Tests/QueryTests.cs
git commit -m "feat: query verbs with degraded-index reporting"
```

---

### Task 9: End-to-end proof on a Razor solution

The tests so far exercise layers. This one asserts the headline claim end to end, because that is the claim the README makes.

**Files:**
- Create: `tests/Vela.Tests/EndToEndTests.cs`

**Interfaces:**
- Consumes: everything above

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Vela.Tests/EndToEndTests.cs
using Microsoft.Data.Sqlite;
using Xunit;

public class EndToEndTests
{
    [Fact]
    public async Task IndexThenRefs_FindsASymbolUsedFromARazorView()
    {
        using var fx = FixtureSolution.CreateWebApp();

        // The scaffolded Index.cshtml uses ViewData, which is declared in C#.
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        Assert.Empty(load.Failures);

        var index = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));

        var razorHits = RefsQuery.Run(db, "ViewData")
            .Where(h => h.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(razorHits);
        // The location must be openable: a .cshtml path, not a .g.cs one.
        Assert.All(razorHits, h => Assert.DoesNotContain(".g.cs", h.RelativePath));
    }
}
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test tests/Vela.Tests --filter EndToEndTests -v q`
Expected: PASS if Tasks 3 to 8 are correct. If it fails, the defect is in `ScipEmitter`'s document keying or `RazorMapper`, not in this test.

- [ ] **Step 3: Commit**

```bash
git add tests/Vela.Tests/EndToEndTests.cs
git commit -m "test: end-to-end proof that Razor references resolve to the view file"
```

---

### Task 10: Ship it

**Files:**
- Create: `install.sh`, `install-codex.sh`, `.github/workflows/ci.yml`
- Modify: `README.md` (replace the placeholder install block if the verbs changed)

**Interfaces:**
- Consumes: the built tool

- [ ] **Step 1: Write CI that asserts the Razor property**

```yaml
# .github/workflows/ci.yml
name: CI
on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet test -v normal
      # The regression that would otherwise be silent.
      - name: Assert generated-document coverage
        run: dotnet test --filter DocumentEnumeratorTests -v normal
```

- [ ] **Step 2: Write install.sh**

```bash
#!/bin/bash
# Install the vela skill into ~/.claude/skills/ and build the vela tool.
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILLS_ROOT="$HOME/.claude/skills"

echo "=== vela skill installer (Claude Code) ==="

command -v dotnet >/dev/null 2>&1 || {
  echo "vela needs the .NET SDK 8.0 or newer: https://dotnet.microsoft.com/download"
  exit 1
}

echo "Building and installing the vela tool..."
dotnet pack "$SCRIPT_DIR/src/Vela/Vela.csproj" -c Release -o "$SCRIPT_DIR/nupkg" >/dev/null
dotnet tool update --global --add-source "$SCRIPT_DIR/nupkg" vela

mkdir -p "$SKILLS_ROOT"
for src in "$SCRIPT_DIR"/skills/*/; do
  src="${src%/}"
  name="$(basename "$src")"
  echo "Installing skill '$name' -> $SKILLS_ROOT/$name"
  ln -sfn "$src" "$SKILLS_ROOT/$name"
done

echo "Done. Run 'vela index' in a solution directory to get started."
```

- [ ] **Step 3: Write install-codex.sh**

```bash
#!/bin/bash
# Install the vela skill into ~/.codex/skills/ for Codex.
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILLS_ROOT="$HOME/.codex/skills"

echo "=== vela skill installer (Codex) ==="

command -v dotnet >/dev/null 2>&1 || {
  echo "vela needs the .NET SDK 8.0 or newer: https://dotnet.microsoft.com/download"
  exit 1
}

dotnet pack "$SCRIPT_DIR/src/Vela/Vela.csproj" -c Release -o "$SCRIPT_DIR/nupkg" >/dev/null
dotnet tool update --global --add-source "$SCRIPT_DIR/nupkg" vela

for src in "$SCRIPT_DIR"/skills/*/; do
  src="${src%/}"
  name="$(basename "$src")"
  target="$SKILLS_ROOT/$name"
  mkdir -p "$target"
  for sub in scripts references tests; do
    [ -d "$src/$sub" ] && ln -sfn "$src/$sub" "$target/$sub"
  done
  sed "s|\${CLAUDE_SKILL_DIR}|$target|g" "$src/SKILL.md" > "$target/SKILL.md"
  echo "Installed '$name' -> $target"
done
```

- [ ] **Step 4: Make them executable and verify the whole path**

```bash
chmod +x install.sh install-codex.sh
./install.sh
cd /tmp && mkdir -p vela-check && cd vela-check
dotnet new webapp -o App --force && dotnet new sln -n Check && dotnet sln Check.sln add App/App.csproj
vela index
vela refs ViewData
```

Expected: `vela refs ViewData` lists at least one `.cshtml` path.

- [ ] **Step 5: Commit**

```bash
git add install.sh install-codex.sh .github/workflows/ci.yml
git commit -m "feat: installers and CI asserting generated-document coverage"
```

---

## Self-review

**Spec coverage.** Every section of `docs/design-notes.md` maps to a task: the four architecture layers to Tasks 2-5 (harvest), 6 (SQLite), 8 (query); the five verbs to Task 8; Constraint 1 to the absence of any network or model dependency throughout; Constraint 2 to Task 6's `IndexPaths` test; Constraint 3 to Task 7 and the degraded-render test in Task 8; Razor and Blazor coverage to Tasks 3, 4 and 9.

**Deferred deliberately, and why.** Two open questions in the spec stay open, because the implementation should inform them rather than the reverse:

- *Incremental reindex.* Task 8's `index` verb rebuilds from scratch. Full rebuild is the correct first implementation; incremental work should be measured against it, not assumed.
- *Staleness beyond load failures.* `IndexHealth` carries a `git_ref` column and Task 7 stores it, but nothing yet compares it to the working tree. Wiring that comparison is a natural Task 11 once real usage shows whether a banner, a non-zero exit, or both is right. The schema is ready for it.

**Known weakness to watch during execution.** `SymbolIdentity.For` uses a Roslyn display string rather than a SCIP-standard symbol moniker. It is sufficient for within-index queries and keeps Task 5 tractable, but it is not interoperable with other SCIP producers. If cross-indexer interoperability becomes a goal, that method is the single place to change, and it should be changed before publishing indexes anywhere.
