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
///
/// One test here points XDG_CACHE_HOME at a directory of its own, and an environment
/// variable is process-wide, so the class sits in the collection that runs alone. The rest
/// of these tests are milliseconds each, so joining it costs nothing.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
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

    /// <summary>
    /// The signature says a string, so null is a caller's bug and is said out loud rather
    /// than handed back as a null the caller's own signature promised it would never see.
    /// Nothing reaches this today; a path key that is quietly null much later, in a hash or
    /// a database row, is exactly the kind of thing that takes a day to trace back here.
    /// The empty string is different: it is a value, not a mistake, and is kept.
    /// </summary>
    [Fact]
    public void ANullPathIsRefused_AndAnEmptyOneIsKept()
    {
        Assert.Throws<ArgumentNullException>(() => RealPath.Of(null!));
        Assert.Equal("", RealPath.Of(""));
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

    /// <summary>
    /// The user-visible bug the case half of this fix exists for: `vela index App.sln` and
    /// `vela refs`, run from a directory the shell spelled with a different case, hashed to
    /// two different cache names, and the second was told there was no index for a solution
    /// the user had just indexed. Two spellings of one file are one index.
    ///
    /// Nothing else in the suite reaches this: the symbolic-link tests cannot, because they
    /// prove a different half, and deleting the body of the case correction left every one
    /// of them green on all three platforms.
    /// </summary>
    [CaseInsensitivePlatformFact]
    public void TwoSpellingsOfOneSolution_AreOneIndex()
    {
        using var tree = new TempTree();

        var repo = Path.Combine(tree.Root, "Repo");
        Directory.CreateDirectory(repo);
        var solution = Path.Combine(repo, "App.sln");
        File.WriteAllText(solution, "");

        // The same file, named the way a second command in the same session named it.
        var shouted = Path.Combine(tree.Root, "REPO", "APP.SLN");

        // XDG_CACHE_HOME is process-wide, which is why this class sits in the collection
        // that runs alone, and it is pinned here so the two answers cannot straddle
        // another test changing it.
        var cacheRoot = Path.Combine(Path.GetTempPath(), "vela-cache-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string asCreated, asShouted;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cacheRoot);
            asCreated = IndexPaths.ForSolution(solution);
            asShouted = IndexPaths.ForSolution(shouted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
        }

        Assert.Equal(asCreated, asShouted);

        // And corrected TO the spelling on disk, not merely to some agreed one: the cache
        // file is named after the solution, and it is named the way the solution is.
        Assert.StartsWith("App-", Path.GetFileName(asShouted), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same fact one layer down, where every other key vela holds is decided: a
    /// pending job's source, an import's health record, a document's path.
    /// </summary>
    [CaseInsensitivePlatformFact]
    public void TwoSpellingsOfOneFile_ResolveToOneString()
    {
        using var tree = new TempTree();

        var directory = Path.Combine(tree.Root, "Repo", "Src");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "Index.scip");
        File.WriteAllText(file, "");

        var shouted = Path.Combine(tree.Root, "REPO", "src", "INDEX.SCIP");

        Assert.Equal(RealPath.Of(file), RealPath.Of(shouted));
        Assert.EndsWith(Path.Combine("Repo", "Src", "Index.scip"), RealPath.Of(shouted), StringComparison.Ordinal);
    }

    /// <summary>
    /// A case-sensitive volume can hold both `Repo` and `repo` at once, and macOS will
    /// mount one on request. .NET's matcher does not care what the volume does:
    /// EnumerateFileSystemInfos matches with MatchCasing.PlatformDefault, which is
    /// case-insensitive on macOS whatever is underneath. Taking the first name that
    /// matches case-insensitively would therefore hand back a DIFFERENT directory -
    /// "/v/Repo/App.sln" for "/v/repo/App.sln" - and vela would index, cache and answer
    /// about a tree nobody named. An exact spelling is on disk exactly when it is the one
    /// that was asked for, so it wins.
    ///
    /// The choice is asserted directly rather than through a real directory holding both
    /// spellings, because no runner in this matrix can hold one: Windows has no
    /// case-sensitive volume to offer, and mounting a case-sensitive image on the macOS
    /// runner would make this fact depend on hdiutil rather than on vela. The list here is
    /// what such a directory offers; which name is taken from it is the whole decision.
    /// </summary>
    [Fact]
    public void AnExactSpellingBeatsOneThatOnlyMatchesWhenCaseIsIgnored()
    {
        Assert.Equal("repo", RealPath.PickOnDiskName("repo", ["Repo", "repo"]));
        Assert.Equal("repo", RealPath.PickOnDiskName("repo", ["repo", "Repo"]));
    }

    [Fact]
    public void ACaseInsensitiveMatchIsTakenOnlyWhenNothingIsSpeltExactly()
    {
        // The Windows and macOS correction this exists for: one directory, two strings,
        // and the string the filesystem stored is the one everything is keyed on.
        Assert.Equal("Repo", RealPath.PickOnDiskName("repo", ["Repo"]));

        // Several to choose from, which only a case-sensitive volume produces, and the
        // answer is still the same one every time: an index cache keyed on it cannot
        // depend on the order a directory happened to be read in.
        Assert.Equal("REPO", RealPath.PickOnDiskName("rEpO", ["Repo", "REPO", "repO"]));
        Assert.Equal("REPO", RealPath.PickOnDiskName("rEpO", ["repO", "REPO", "Repo"]));

        // Nothing matched at all: the spelling that was asked for is kept, which is what
        // lets a path that does not exist yet still have a name.
        Assert.Equal("repo", RealPath.PickOnDiskName("repo", ["other", "another"]));
        Assert.Equal("repo", RealPath.PickOnDiskName("repo", []));
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
