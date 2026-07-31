using System.CommandLine;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// An index vela cannot read is a sentence, not a stack trace.
///
/// The index is a cache file in a directory nobody is asked to look after. It can be
/// truncated by a full disk part way through a copy, damaged by the filesystem underneath
/// it, or replaced by something that is not a database at all, and every verb that opens
/// one used to answer that with a raw `SqliteException: file is not a database` and a .NET
/// stack trace. That is Constraint 3's exact failure in the container rather than in the
/// contents: the index cannot be read, so it has to say so, in words, and name the one
/// command that fixes it.
///
/// These tests drive the real commands with the real exception handler in place, which is
/// what was never exercised: the suite disables that handler, so the production path was
/// only ever reached by a user. They share the non-parallel collection with every other
/// test that resolves an index through XDG_CACHE_HOME.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class UnreadableIndexTests
{
    [Fact]
    public async Task Refs_ReportsAnIndexItCannotReadRatherThanThrowing()
    {
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        Damage(indexPath);

        var result = await InvokeAsync("refs", "Solo.Thing.Value()", "--solution", fx.SolutionPath);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("could not be read", result.Output);
        Assert.Contains(indexPath, result.Output);
        Assert.Contains("vela index --solution", result.Output);

        // No answer, and no half-answer either. A query that reported hits from an index it
        // could not vouch for is the thing this exists to prevent.
        Assert.DoesNotContain("Unhandled exception", result.Output);
        Assert.DoesNotContain("   at ", result.Output);
        Assert.DoesNotContain("Solo/Thing.cs", result.Output);
    }

    [Fact]
    public async Task Find_ReportsAnIndexItCannotReadRatherThanThrowing()
    {
        // find answers with names rather than hits and opens the index by the same route,
        // so it has to reach the same sentence. "0 symbol(s)" from a damaged index would be
        // the worst possible answer: an authoritative "no such name exists".
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);
        Damage(IndexPaths.ForSolution(fx.SolutionPath));

        var result = await InvokeAsync("find", "Value", "--solution", fx.SolutionPath);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("could not be read", result.Output);
        Assert.DoesNotContain("symbol(s)", result.Output);
        Assert.DoesNotContain("Unhandled exception", result.Output);
    }

    [Fact]
    public async Task Import_ReportsAnIndexItCannotReadRatherThanThrowing()
    {
        // import opens an existing index to check its schema version before it writes a
        // row, so a damaged one reaches it in the same place and must not leave the caller
        // wondering whether anything went in.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        Damage(indexPath);

        var scip = Path.Combine(Path.GetDirectoryName(fx.SolutionPath)!, "other.scip");
        File.WriteAllBytes(scip, new byte[] { 0x0a, 0x00 });

        var result = await InvokeAsync("import", scip, "--solution", fx.SolutionPath);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.Contains("could not be read", result.Output);
        Assert.Contains("Nothing was imported", result.Output);
        Assert.Contains("vela index --solution", result.Output);
        Assert.DoesNotContain("Unhandled exception", result.Output);
        Assert.DoesNotContain("   at ", result.Output);
    }

    [Fact]
    public async Task Index_RebuildsOverAnIndexItCannotRead()
    {
        // The other half, and the reason the message names this command: `vela index`
        // builds from nothing and moves the new file over the damaged one, so the advice
        // the query verbs give is advice that works.
        using var fx = FixtureSolution.CreateLibrary();
        using var cache = new TempCacheHome();

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var indexPath = IndexPaths.ForSolution(fx.SolutionPath);
        Damage(indexPath);

        Assert.Equal(0, (await InvokeAsync("index", "--solution", fx.SolutionPath)).ExitCode);

        var query = await InvokeAsync("refs", "Solo.Thing.Value()", "--solution", fx.SolutionPath);
        Assert.Equal(0, query.ExitCode);
        Assert.Contains("Solo/Thing.cs", query.Output);
    }

    /// <summary>
    /// The same shape one level up: not an index vela cannot read, but the directory they
    /// all live in. `vela cache` is the verb whose whole job is to say what is in there,
    /// so it is the one that meets a directory it cannot list, and it used to answer with
    /// a raw UnauthorizedAccessException and a stack trace.
    /// </summary>
    [UnixOnlyFact]
    public async Task Cache_ReportsACacheDirectoryItCannotReadRatherThanThrowing()
    {
        using var cache = new TempCacheHome();

        var directory = IndexPaths.CacheDirectory();
        Directory.CreateDirectory(directory);

        var mode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.None);

        try
        {
            var listed = await InvokeAsync("cache");

            Assert.Equal(Program.ExitCannotAnswer, listed.ExitCode);
            Assert.Contains("could not be read", listed.Output);
            Assert.Contains(directory, listed.Output);
            Assert.DoesNotContain("Unhandled exception", listed.Output);
            Assert.DoesNotContain("   at ", listed.Output);

            // The verb that removes things has to be at least as careful: refusing is
            // fine, a stack trace after an unknown number of deletions is not.
            var cleared = await InvokeAsync("cache", "clear", "--all");

            Assert.Equal(Program.ExitCannotAnswer, cleared.ExitCode);
            Assert.Contains("could not be read", cleared.Output);
            Assert.DoesNotContain("Unhandled exception", cleared.Output);
        }
        finally
        {
            File.SetUnixFileMode(directory, mode);
        }
    }

    /// <summary>
    /// Replaces an index with bytes that are not a database. Not truncation, because a
    /// truncated file still carries a readable header and this has to be the plain case:
    /// SQLite opens the file lazily and refuses at the first read.
    /// </summary>
    private static void Damage(string indexPath)
    {
        var rubbish = new byte[64 * 1024];
        Random.Shared.NextBytes(rubbish);
        File.WriteAllBytes(indexPath, rubbish);
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

    /// <summary>Points XDG_CACHE_HOME at a disposable directory, and puts it back.</summary>
    private sealed class TempCacheHome : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TempCacheHome()
        {
            _path = Path.Combine(Path.GetTempPath(), "vela-unreadable-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_path);
            _previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _previous);
            try { Directory.Delete(_path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
