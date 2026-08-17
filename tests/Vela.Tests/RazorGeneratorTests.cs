using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Vela.Harvest;
using Vela.Tests.Fixtures;
using Xunit;

/// <summary>
/// The guard on the one way vela has already lost its whole reason to exist.
///
/// On .NET SDK 10.0.400 the Razor generator vela loads out of the SDK was built against
/// Microsoft.CodeAnalysis 5.9.0.0, vela hosted 5.6.0.0, and Roslyn refused to load it
/// without raising anything: zero generators, zero Razor documents, a healthy-looking
/// index with no .cshtml or .razor in it. These tests hold both halves of the answer.
/// The first is that the compiler vela hosts must not fall behind the one the SDK's
/// generator wants, which is a race vela can lose again on any SDK feature band. The
/// second is that if it does lose it, the index says so.
/// </summary>
public class RazorGeneratorTests : IClassFixture<HarvestedWebApp>
{
    private readonly HarvestedWebApp _webApp;

    public RazorGeneratorTests(HarvestedWebApp webApp) => _webApp = webApp;

    [Fact]
    public void HostedCompiler_IsAtLeastTheOneTheSdksRazorGeneratorWasBuiltAgainst()
    {
        var razor = _webApp.Project.AnalyzerReferences
            .FirstOrDefault(reference => Path.GetFileName(reference.FullPath ?? "")
                .Equals("Microsoft.CodeAnalysis.Razor.Compiler.dll", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(razor);

        var required = CompilerReferencedBy(razor!.FullPath!);
        var hosted = typeof(SyntaxTree).Assembly.GetName().Version;

        Assert.NotNull(required);
        Assert.NotNull(hosted);

        // Not >=, but the whole reason the failure is silent: Roslyn's rule is one
        // directional, so a hosted compiler that is older by any amount loads no Razor
        // generator at all, and one that is newer is fine.
        Assert.True(hosted >= required,
            $"The Razor generator in '{razor.FullPath}' is built against Microsoft.CodeAnalysis "
            + $"{required} and vela hosts {hosted}. Roslyn will not load a generator built against "
            + "a newer compiler, so every .cshtml and .razor file is about to vanish from the "
            + "index without an error. Raise the Microsoft.CodeAnalysis.* pin in Vela.csproj to "
            + $"{required} or later. See docs/upstream/razor-sdk-10-0-400.md.");
    }

    [Fact]
    public void Diagnose_WhenAProjectsViewsDidNotReachTheIndex_NamesTheProjectAndTheCount()
    {
        var note = RazorSourceGenerator.Diagnose(_webApp.Project, generatedViews: 0);

        Assert.NotNull(note);
        Assert.StartsWith(RazorSourceGenerator.NotePrefix, note);
        Assert.Contains(_webApp.Project.Name, note);
        Assert.Contains(_webApp.RazorFileCount.ToString(), note);
    }

    [Fact]
    public void Diagnose_WhenTheViewsCameThrough_SaysNothing() =>
        Assert.Null(RazorSourceGenerator.Diagnose(_webApp.Project, _webApp.RazorFileCount));

    [Fact]
    public async Task Diagnose_OnAProjectWithNoViewsAtAll_SaysNothing()
    {
        // A plain C# library generates no views because it has none, which is not a
        // problem and must never be reported as one, or every non-web solution indexes
        // itself degraded.
        using var fx = FixtureSolution.CreateProjectGraph();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        foreach (var project in load.Solution.Projects)
        {
            Assert.Equal(0, RazorSourceGenerator.ViewCount(project));
            Assert.Null(RazorSourceGenerator.Diagnose(project, generatedViews: 0));
        }
    }

    [Fact]
    public void ViewCount_CountsTheViewsTheCompilerWasHanded()
    {
        // The .cshtml files are additional documents, never Documents. If this ever
        // matches project.Documents, the compiler stopped being given the views.
        Assert.Equal(_webApp.RazorFileCount, RazorSourceGenerator.ViewCount(_webApp.Project));
        Assert.True(_webApp.RazorFileCount > 0, "fixture must contain .cshtml files");
    }

    [Fact]
    public void AHealthyHarvest_LeavesNoRazorNote()
    {
        // The other side of the guard: the note must not fire on a solution whose views
        // did come through, or it is noise and stops being read.
        Assert.DoesNotContain(_webApp.Emitted.Notes,
            note => note.Note.StartsWith(RazorSourceGenerator.NotePrefix, StringComparison.Ordinal));
    }

    private static Version? CompilerReferencedBy(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();

        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (metadata.GetString(reference.Name) == "Microsoft.CodeAnalysis")
                return reference.Version;
        }

        return null;
    }
}
