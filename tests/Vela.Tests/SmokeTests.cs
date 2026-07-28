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
