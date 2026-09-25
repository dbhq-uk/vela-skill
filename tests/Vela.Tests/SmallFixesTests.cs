using System.CommandLine;
using Google.Protobuf;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// The smaller fixes from the 24 Sep review: a cap on long answers, a private index cache,
/// and `impls`, which answers "what implements this".
///
/// Several of these index through the CLI, which resolves the cache from VELA_CACHE_HOME,
/// so the class shares the non-parallel collection.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class SmallFixesTests
{
    private static readonly HealthRecord Healthy = new(DateTime.UtcNow, null, false, null);

    private static IReadOnlyList<Hit> ManyHits() => Enumerable.Range(0, 7)
        .Select(i => new Hit(i < 4 ? "App/A.cs" : "App/B.cs", i, 0, "App.Thing.Name", false))
        .ToList();

    // ---- --limit and --files --------------------------------------------------------

    [Fact]
    public void Render_WithALimit_PrintsThatManyAndSaysHowManyMoreThereAre()
    {
        // An ordinary name answered with thousands of rows on a real solution, with no way
        // to ask for fewer. The count is still the whole count, so a total is never
        // mistaken for the list, and a line says how many were cut.
        var output = OutputWriter.Render(ManyHits(), Healthy, limit: 3);

        Assert.Equal(3, output.Split('\n').Count(line => line.Contains("ref  App.Thing.Name")));
        Assert.Contains("7 result(s)", output, StringComparison.Ordinal);
        Assert.Contains("4 more not shown", output, StringComparison.Ordinal);
        Assert.DoesNotContain("App/B.cs", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithALimitAboveTheCount_SaysNothingWasCut()
    {
        var output = OutputWriter.Render(ManyHits(), Healthy, limit: 50);

        Assert.Equal(7, output.Split('\n').Count(line => line.Contains("ref  App.Thing.Name")));
        Assert.DoesNotContain("more not shown", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ByFile_PrintsACountPerFileInsteadOfTheHits()
    {
        var output = OutputWriter.Render(ManyHits(), Healthy, byFile: true);

        Assert.Matches(@"(?m)^\s+4  App/A\.cs\r?$", output);
        Assert.Matches(@"(?m)^\s+3  App/B\.cs\r?$", output);
        Assert.DoesNotContain("ref  App.Thing.Name", output, StringComparison.Ordinal);
        Assert.Contains("7 result(s) in 2 file(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithALimit_KeepsTheBanner()
    {
        var degraded = new HealthRecord(DateTime.UtcNow, null, true, "stale index: something changed");

        var output = OutputWriter.Render(ManyHits(), degraded, limit: 1);

        Assert.StartsWith("!! INCOMPLETE INDEX", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refs_WithANegativeLimit_IsRefused()
    {
        var result = await InvokeAsync("refs", "Anything", "--limit", "-1", "--solution", "Nowhere.sln");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--limit must be 0 or more", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refs_WithALimit_KeepsTheAmbiguityBlockAfterTheCut()
    {
        // The block qualifies the whole answer, so it is never the part that gets cut.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        fx.Write("Solo/Shapes.cs", Shapes);
        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var refs = await InvokeAsync("refs", "Area", "--limit", "1", "--solution", fx.SolutionPath);

        Assert.Equal(0, refs.ExitCode);
        Assert.Contains("more not shown", refs.Output, StringComparison.Ordinal);
        Assert.Contains("'Area' is ambiguous", refs.Output, StringComparison.Ordinal);
    }

    // ---- A private cache ------------------------------------------------------------

    [UnixOnlyFact]
    public async Task Index_CreatesTheCacheDirectoryAt700AndTheIndexAt600()
    {
        // An index names every symbol and path in the code it covers. The directory was
        // created 755 and the database 644, readable by anybody on the machine.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var index = IndexPaths.ForSolution(fx.SolutionPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                     File.GetUnixFileMode(Path.GetDirectoryName(index)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(index));

        // An incremental rebuild builds into a copy, and the copy is private too.
        Assert.Equal(0, (await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath)).ExitCode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(index));
    }

    [UnixOnlyFact]
    public async Task Index_NarrowsACacheDirectoryThatIsAlreadyOpen()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        var directory = Path.Combine(cache.Path, "vela");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, (UnixFileMode)Convert.ToInt32("755", 8));

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                     File.GetUnixFileMode(directory));
    }

    [UnixOnlyFact]
    public async Task Import_IntoNothing_CreatesTheIndexAt600()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                ProjectRoot = new Uri(fx.Root + Path.DirectorySeparatorChar).AbsoluteUri,
                ToolInfo = new Scip.ToolInfo { Name = "scip-typescript", Version = "0.4.0" }
            }
        };
        var scip = Path.Combine(fx.Root, "web.scip");
        File.WriteAllBytes(scip, index.ToByteArray());

        Assert.Equal(0, (await InvokeAsync("import", scip, "--solution", fx.SolutionPath)).ExitCode);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                     File.GetUnixFileMode(IndexPaths.ForSolution(fx.SolutionPath)));
    }

    // ---- impls ----------------------------------------------------------------------

    private const string Shapes = """
        namespace Solo.Shapes
        {
            public interface IShape
            {
                double Area();
                string Name { get; }
            }

            public abstract class ShapeBase : IShape
            {
                public abstract double Area();
                public virtual string Name => "shape";
            }

            public sealed class Square : ShapeBase
            {
                public override double Area() => 4;
                public override string Name => "square";
            }

            public sealed class Circle : IShape
            {
                double IShape.Area() => 3.14;
                public string Name => "circle";
            }

            public static class Unrelated
            {
                public static double Area() => 0;
            }
        }
        """;

    [Fact]
    public async Task Impls_AnswersWhatImplementsAnInterfaceAndItsMembers()
    {
        // "What implements this" was listed as something vela could not answer.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        fx.Write("Solo/Shapes.cs", Shapes);
        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        // A type: directly, and through a base class.
        var types = await InvokeAsync("impls", "IShape", "--solution", fx.SolutionPath);
        Assert.Equal(0, types.ExitCode);
        Assert.Contains("def  Solo.Shapes.ShapeBase", types.Output, StringComparison.Ordinal);
        Assert.Contains("def  Solo.Shapes.Square", types.Output, StringComparison.Ordinal);
        Assert.Contains("def  Solo.Shapes.Circle", types.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated", types.Output, StringComparison.Ordinal);
        Assert.Contains("3 result(s)", types.Output, StringComparison.Ordinal);

        // A member: implicitly, and explicitly. An override of the implicit one implements
        // the interface member too, because it is what the interface call reaches.
        var members = await InvokeAsync("impls", "IShape.Area", "--solution", fx.SolutionPath);
        Assert.Contains("Solo.Shapes.ShapeBase.Area()", members.Output, StringComparison.Ordinal);
        Assert.Contains("Solo.Shapes.Circle.Area()", members.Output, StringComparison.Ordinal);
        Assert.Contains("Solo.Shapes.Square.Area()", members.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated", members.Output, StringComparison.Ordinal);

        // An override of a base class member.
        var overrides = await InvokeAsync("impls", "ShapeBase.Name", "--solution", fx.SolutionPath);
        Assert.Contains("Solo.Shapes.Square.Name", overrides.Output, StringComparison.Ordinal);
        Assert.Contains("1 result(s)", overrides.Output, StringComparison.Ordinal);

        // A name that is two symbols says so, as refs does.
        var ambiguous = await InvokeAsync("impls", "Area", "--solution", fx.SolutionPath);
        Assert.Contains("'Area' is ambiguous", ambiguous.Output, StringComparison.Ordinal);

        // Something nothing implements says why the answer is empty.
        var none = await InvokeAsync("impls", "Unrelated.Area", "--solution", fx.SolutionPath);
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("nothing vela harvested implements", none.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impls_AfterAnIncrementalRebuild_DescribesTheCodeAsItIsNow()
    {
        // The rows hang off the document of the definition, so a rebuild that replaces the
        // document replaces them. Left behind, they would name a class that is gone.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();
        fx.Write("Solo/Shapes.cs", Shapes);
        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        fx.Write("Solo/Shapes.cs", Shapes.Replace("public sealed class Circle : IShape", "public sealed class Circle"));
        var rebuilt = await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath);
        Assert.True(rebuilt.ExitCode is 0 or 3, rebuilt.Output);

        var types = await InvokeAsync("impls", "IShape", "--solution", fx.SolutionPath);
        Assert.DoesNotContain("def  Solo.Shapes.Circle", types.Output, StringComparison.Ordinal);
        Assert.Contains("def  Solo.Shapes.Square", types.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_WritesImplementationRelationshipsIntoTheScip()
    {
        // The same facts in scip.proto's own terms, so the index answers "go to
        // implementations" for any consumer and not only for vela.
        using var fx = FixtureSolution.CreateLibrary();
        fx.Write("Solo/Shapes.cs", Shapes);
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        var emitted = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        var shapes = emitted.Index.Documents.Single(d => d.RelativePath == "Solo/Shapes.cs");
        var circle = shapes.Symbols.Single(s => s.Symbol.EndsWith("/Circle#", StringComparison.Ordinal));

        var relationship = Assert.Single(circle.Relationships);
        Assert.True(relationship.IsImplementation);
        Assert.EndsWith("/IShape#", relationship.Symbol, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        using var writer = new StringWriter();
        var configuration = new InvocationConfiguration
        {
            Output = writer,
            Error = writer,
            EnableDefaultExceptionHandler = false
        };

        var exitCode = await Program.BuildRootCommand().Parse(args).InvokeAsync(configuration);
        return (exitCode, writer.ToString());
    }
}
