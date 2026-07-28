using System.CommandLine;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        BuildRootCommand().Parse(args).InvokeAsync();

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Compiler-exact code search for .NET.");
        foreach (var name in new[] { "index", "find", "def", "refs", "outline", "impact" })
            root.Add(new Command(name));
        return root;
    }
}
