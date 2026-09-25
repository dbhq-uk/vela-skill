using System.CommandLine;
using Xunit;

public class SmokeTests
{
    [Fact]
    public async Task RootCommand_WithNoArguments_ExitsNonZeroAndPrintsHelp()
    {
        var root = Program.BuildRootCommand();
        var exit = await root.Parse(Array.Empty<string>()).InvokeAsync();
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Help_DescribesASCIPToolThatIndexesDotNet()
    {
        // `--help` said "code search for .NET" after vela had become a SCIP tool that
        // imports any language, which undersold it to anyone deciding whether to use it.
        using var writer = new StringWriter();
        var configuration = new InvocationConfiguration { Output = writer, Error = writer };

        var exit = await Program.BuildRootCommand().Parse(new[] { "--help" }).InvokeAsync(configuration);

        Assert.Equal(0, exit);
        Assert.Contains("SCIP", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains(".NET", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("for .NET", writer.ToString(), StringComparison.Ordinal);
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
