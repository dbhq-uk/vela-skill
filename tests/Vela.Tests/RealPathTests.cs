using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

namespace Vela.Tests;

/// <summary>
/// One spelling for one file.
///
/// vela keys a pending job, an import's health record, a document's source and the index
/// cache itself on an absolute path, and every one of those keys is only an identity if
/// two ways of naming one file produce one string. Path.GetFullPath does not give that:
/// it removes '.', '..' and a relative prefix and stops.
/// </summary>
public class RealPathTests
{
    [Fact]
    public void APathWithNothingToResolve_IsTheFullPath()
    {
        using var tree = new TempTree();

        var file = Path.Combine(tree.Root, "a", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");

        Assert.Equal(RealPath.Of(tree.Root), Path.GetDirectoryName(Path.GetDirectoryName(RealPath.Of(file))));
        Assert.True(Path.IsPathRooted(RealPath.Of(file)));
    }

    [Fact]
    public void ARelativePathBecomesAbsolute_AndTheDotsGo()
    {
        var resolved = RealPath.Of(Path.Combine(".", "some", "..", "thing"));

        Assert.True(Path.IsPathRooted(resolved));
        Assert.DoesNotContain("..", resolved, StringComparison.Ordinal);
        Assert.EndsWith("thing", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void APathThatIsNotThereYet_KeepsItsNameAndResolvesWhatIsAboveIt()
    {
        using var tree = new TempTree();

        // The pending-job case exactly: the .scip is keyed before the indexer that
        // produces it has ever run, so the leaf does not exist and still needs a spelling.
        var missing = Path.Combine(tree.Root, "not-there-yet.scip");

        Assert.Equal(Path.Combine(RealPath.Of(tree.Root), "not-there-yet.scip"), RealPath.Of(missing));
    }

    [SymbolicLinkFact]
    public void ALinkResolvesToItsTarget()
    {
        using var tree = new TempTree();

        var target = Path.Combine(tree.Root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "");

        var link = Path.Combine(tree.Root, "link");
        Directory.CreateSymbolicLink(link, target);

        Assert.Equal(
            RealPath.Of(Path.Combine(target, "file.txt")),
            RealPath.Of(Path.Combine(link, "file.txt")));
    }

    /// <summary>
    /// The shape macOS produces without anybody asking for it, and the one that got this
    /// wrong the first time: a link whose recorded TARGET is itself reached through
    /// another link. FileSystemInfo.ResolveLinkTarget follows a chain of links, but it
    /// hands back the target as recorded, so a target beginning "/var" stays beginning
    /// "/var" even though "/var" is a link to "/private/var". Resolving it only once
    /// leaves two spellings of one file, which is the whole bug.
    /// </summary>
    [SymbolicLinkFact]
    public void ALinkWhoseTargetIsItselfReachedThroughALink_ResolvesAllTheWay()
    {
        using var tree = new TempTree();

        var real = Path.Combine(tree.Root, "real");
        Directory.CreateDirectory(Path.Combine(real, "repo"));

        // outer -> tree/real, then inner -> outer/repo, so inner's recorded target names
        // a path that only exists through outer.
        var outer = Path.Combine(tree.Root, "outer");
        Directory.CreateSymbolicLink(outer, real);

        var inner = Path.Combine(tree.Root, "inner");
        Directory.CreateSymbolicLink(inner, Path.Combine(outer, "repo"));

        Assert.Equal(RealPath.Of(Path.Combine(real, "repo")), RealPath.Of(inner));
        Assert.DoesNotContain("outer", RealPath.Of(inner), StringComparison.Ordinal);
    }

    [SymbolicLinkFact]
    public void ABrokenLinkIsKeptRatherThanRefused()
    {
        using var tree = new TempTree();

        var link = Path.Combine(tree.Root, "dangling");
        Directory.CreateSymbolicLink(link, Path.Combine(tree.Root, "never-existed"));

        // Nothing to resolve to, and a path vela cannot resolve is still a path it has to
        // be able to name: refusing here would fail an index over a stray link.
        Assert.False(string.IsNullOrEmpty(RealPath.Of(link)));
    }

    /// <summary>A directory of its own, removed when the test finishes. Private here for
    /// the same reason every other test class keeps its own: they are one line each and
    /// sharing one would couple classes that have nothing else to say to each other.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; }

        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "vela-rp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
