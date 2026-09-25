using System.CommandLine;
using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Query;
using Vela.Tests.Fixtures;
using Xunit;

// Indexing through the CLI resolves the index path from VELA_CACHE_HOME, which is
// process-wide, so this class shares the non-parallel collection with every other test
// that touches it.
[Collection(EnvironmentSensitive.Name)]
public class EndToEndTests
{
    [Fact]
    public async Task IndexThenRefs_FindsASymbolUsedFromARazorView()
    {
        using var fx = FixtureSolution.CreateWebApp();

        // The scaffolded Index.cshtml uses ViewData, which is declared in C#.
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);
        Assert.Empty(load.Failures);

        var emitted = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, emitted);
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));

        var razorHits = RefsQuery.Run(db, "ViewData")
            .Where(h => h.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(razorHits);
        // The location must be openable: a .cshtml path, not a .g.cs one.
        Assert.All(razorHits, h => Assert.DoesNotContain(".g.cs", h.RelativePath));
    }

    [Fact]
    public async Task IndexWithStats_ReportsTheCoverageThatMustNotRegress()
    {
        // AGENTS.md prescribes `vela index --stats` as the way to check the property
        // this whole tool exists for, and there was no such option: the instruction
        // named a command nobody could have run.
        //
        // The property itself is why the option is worth having. Razor views and Blazor
        // components reach the compiler as source-generated documents, and a regression
        // that loses them is silent - the index still builds, queries still answer, and
        // the Razor half of the codebase quietly disappears. So the numbers are
        // asserted here, by count, through the real command.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var result = await InvokeAsync("index", "--stats", "--solution", fx.SolutionPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("documents", result.Output, StringComparison.Ordinal);
        Assert.Contains("generated", result.Output, StringComparison.Ordinal);
        Assert.Contains("razor", result.Output, StringComparison.Ordinal);
        Assert.Contains("occurrences", result.Output, StringComparison.Ordinal);
        Assert.Contains("definitions", result.Output, StringComparison.Ordinal);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        Assert.True(File.Exists(indexPath), result.Output);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = indexPath, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();

        var stats = IndexStatistics.Read(db);

        // One document per view, and the fixture must actually have views or the
        // assertion above is vacuous.
        Assert.True(fx.RazorFileCount > 0, "fixture must contain .cshtml files");
        Assert.Equal(fx.RazorFileCount, stats.RazorDocuments);

        // Seeded but empty Razor documents would satisfy the count above, so the views
        // have to carry occurrences too.
        Assert.True(stats.RazorOccurrences > 0,
            "Razor documents exist but carry no occurrences, which is the whole point of the tool");

        Assert.True(stats.GeneratedDocuments > 0, "the Razor generator's output must be indexed and marked");
        Assert.True(stats.Documents > stats.RazorDocuments);
        Assert.True(stats.Definitions > 0);
        Assert.True(stats.Occurrences > stats.Definitions);
    }

    [Fact]
    public async Task Impact_OnTheScaffold_SaysTheRazorReferenceHasNoCaller()
    {
        // The scaffold's Error page model declares RequestId, uses it in C# from OnGet and
        // ShowRequestId, and Error.cshtml uses it too. impact named the two C# callers at
        // exit 0 and left the view out without a word, so "what breaks if I change this"
        // came back as a partial list that read as the whole of it.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var refs = await InvokeAsync("refs", "RequestId", "--solution", fx.SolutionPath);
        Assert.Contains("Error.cshtml", refs.Output, StringComparison.Ordinal);

        var impact = await InvokeAsync("impact", "RequestId", "--solution", fx.SolutionPath);

        Assert.Equal(0, impact.ExitCode);
        Assert.Contains("OnGet", impact.Output, StringComparison.Ordinal);
        Assert.Matches(@"\d+ reference\(s\) could not be attributed to a caller", impact.Output);
    }

    [Fact]
    public async Task Blazor_ReportsAMemberAParameterAndAComponentTagAgainstTheRazorFile()
    {
        // Every other end-to-end test uses .cshtml, so nothing asserted that a .razor
        // location survives the whole path from harvest to output. And one case did not:
        // a component's own definition and every use of it by tag exist only in #line
        // hidden code, so `refs Badge` answered 0 by default and `def Badge` named a .g.cs
        // file nobody can open.
        using var fx = FixtureSolution.CreateBlazorApp();
        using var cache = new TempCacheHome();

        fx.Write("App/Components/Badge.razor", """
            <span class="badge">@Label</span>

            @code {
                [Parameter] public string? Label { get; set; }
            }
            """);
        fx.Write("App/Components/Pages/Home.razor", """
            @page "/"

            <PageTitle>Home</PageTitle>

            <h1>Hello, world!</h1>

            <Badge Label="new" />
            <Badge />
            """);

        var index = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(0, index.ExitCode);

        // A member: the scaffold's Counter.razor calls IncrementCount from @onclick.
        var member = Rows((await InvokeAsync("refs", "Counter.IncrementCount", "--solution", fx.SolutionPath)).Output);
        Assert.Contains(member, row => row.Path == "App/Components/Pages/Counter.razor" && row.Position != "file"
                                       && row.Kind == "ref");

        // A parameter: Label="new" on line 7 of Home.razor, column 8.
        var parameter = Rows((await InvokeAsync("refs", "Badge.Label", "--solution", fx.SolutionPath)).Output);
        Assert.Contains(parameter, row => row.Path == "App/Components/Pages/Home.razor" && row.Position == "7:8");

        // A component tag, with no --include-generated. The generator records no line for
        // it, so the file is the claim and the output says `file` rather than inventing one.
        var tag = await InvokeAsync("refs", "Components.Badge", "--solution", fx.SolutionPath);
        Assert.Equal(0, tag.ExitCode);
        Assert.Contains(Rows(tag.Output), row => row.Path == "App/Components/Pages/Home.razor"
                                                 && row.Position == "file" && row.Kind == "ref"
                                                 && row.Symbol == "Shop.Components.Badge");
        Assert.DoesNotContain(".g.cs", tag.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("further result(s) in generated code", tag.Output, StringComparison.Ordinal);

        // The definition is the .razor file.
        var def = await InvokeAsync("def", "Components.Badge", "--solution", fx.SolutionPath);
        var defined = Assert.Single(Rows(def.Output));
        Assert.Equal(("App/Components/Badge.razor", "file", "def"), (defined.Path, defined.Position, defined.Kind));
        Assert.DoesNotContain(".g.cs", def.Output, StringComparison.Ordinal);

        // And an incremental rebuild writes the same rows: it loads through a second path.
        fx.Write("App/Components/Pages/Home.razor", fx.Read("App/Components/Pages/Home.razor") + "\n<Badge Label=\"again\" />\n");
        Assert.Equal(0, (await InvokeAsync("index", "--incremental", "--solution", fx.SolutionPath)).ExitCode);
        var afterRebuild = Rows((await InvokeAsync("refs", "Components.Badge", "--solution", fx.SolutionPath)).Output);
        Assert.Contains(afterRebuild, row => row.Path == "App/Components/Pages/Home.razor" && row.Position == "file");
    }

    /// <summary>
    /// The hit rows of a rendered answer: the file each sits under, its position (`line:col`
    /// or `file`), def or ref, and the symbol.
    /// </summary>
    private static List<(string Path, string Position, string Kind, string Symbol)> Rows(string output)
    {
        var rows = new List<(string, string, string, string)>();
        string? path = null;
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var hit = System.Text.RegularExpressions.Regex.Match(line, @"^\s+(file|\d+:\d+)\s+(def|ref)  (.+)$");
            if (hit.Success && path is not null)
                rows.Add((path, hit.Groups[1].Value, hit.Groups[2].Value, hit.Groups[3].Value));
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.Contains('/'))
                path = line.Split("  ")[0];
            else if (line.Length == 0)
                path = null;
        }

        return rows;
    }

    [Fact]
    public async Task Index_RecordsWhatEachProjectWasBuiltFrom()
    {
        // The ledger has to be written by the command people actually run, not only by
        // the emitter a test can call. Without it there is nothing on disk for a later
        // run to compare a tree against, and an incremental rebuild would have to guess.
        using var fx = FixtureSolution.CreateWebApp();
        using var cache = new TempCacheHome();

        var result = await InvokeAsync("index", "--solution", fx.SolutionPath);
        Assert.Equal(0, result.ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = indexPath, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();

        var recorded = ProjectInputs.Read(db);
        var project = Assert.Single(recorded);

        Assert.Equal("App/App.csproj", project.Project);
        Assert.Matches("^[0-9a-f]{64}$", project.Fingerprint);
        Assert.Equal(Schema.Version, project.SchemaVersion);
        Assert.NotEmpty(project.VelaVersion);

        // Every .cshtml the fixture holds is one of the inputs the project was built
        // from, because a view is the input its generated document is derived from.
        var views = ProjectInputs.ReadInputs(db, project.Project)
            .Where(i => i.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(fx.RazorFileCount, views.Count);
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
