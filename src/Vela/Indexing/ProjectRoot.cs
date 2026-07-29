namespace Vela.Indexing;

/// <summary>
/// The directory an index covers: the root every document path in it is relative to,
/// and the root the freshness check has to watch.
///
/// One definition, read from two places. ScipEmitter roots the emitted index here, and
/// Staleness starts its walk at this same directory, so no file can be indexed from
/// outside the tree the freshness check covers. It could: the index widened to the
/// repository root while the walk still started at the solution directory, so in the
/// ordinary `repo/src/App.sln` layout every file under `repo/tests/` was indexed and
/// none of it was watched. Editing one left every verb answering at exit 0, with no
/// banner, at line numbers that had moved, which is the exact shape Constraint 3 exists
/// to forbid.
///
/// Sharing the root makes the two trees the same tree. It does not make the two SETS of
/// files the same set, and they are not: Staleness watches only the extensions vela
/// indexes and skips several directories outright, so it is a documented subset. See
/// <see cref="Staleness"/> for what that costs and where it is stated to the reader.
/// </summary>
public static class ProjectRoot
{
    /// <summary>The root for a solution file, which need not exist on disk.</summary>
    public static string ForSolution(string solutionPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        return ForSolutionDirectory(string.IsNullOrEmpty(directory)
            ? Directory.GetCurrentDirectory()
            : directory);
    }

    /// <summary>
    /// The git repository root the directory sits in, or the directory itself when it
    /// sits in none.
    ///
    /// The repository is the unit a developer works in: a `repo/src/App.sln` layout is
    /// ordinary, and rooting the index at `repo/src` stranded everything above it, so a
    /// shared view or a linked file one directory up could not be a document at all and
    /// was reported as missing on every query.
    /// </summary>
    public static string ForSolutionDirectory(string solutionDirectory)
    {
        var directory = Path.GetFullPath(solutionDirectory);
        return FindRepositoryRoot(directory) ?? directory;
    }

    /// <summary>
    /// The root of the git working tree a directory sits in, or null when it sits in
    /// none.
    ///
    /// A walk up the directory chain looking for a `.git` entry, rather than a call to
    /// git: the walk is deterministic (Constraint 1), needs nothing installed, and
    /// cannot be affected by a git configuration vela did not set. A `.git` DIRECTORY is
    /// an ordinary clone; a `.git` FILE holds a `gitdir:` pointer and means a linked
    /// worktree or a submodule, both of which are working trees whose root this is. Only
    /// the first, innermost one counts, so a submodule is rooted at itself rather than
    /// at the repository containing it.
    /// </summary>
    private static string? FindRepositoryRoot(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return current.FullName;
        }

        return null;
    }
}
