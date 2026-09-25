namespace Vela.Indexing;

/// <summary>
/// What an index is keyed on when the repository it covers has no solution at all.
///
/// Every index was keyed on a solution file, so a repository with no .NET in it - one whose
/// only languages arrive through `vela import` - could only be given an index by inventing a
/// solution: `--solution whatever.sln` on every command, which is what the documentation told
/// people to type. The made-up path also looked deleted to the cache, which removes the index
/// of a solution that has gone, so the next `vela index` of any other solution could throw
/// the import away.
///
/// The key is the repository's own `.git` entry. It exists exactly when there is a
/// repository root, and every path vela derives from a solution is derived from its parent,
/// which for this key is the repository root itself: the root the index is relative to, the
/// directory the freshness walk starts at, and the check that the cache is not inside the
/// tree being indexed. So the rest of vela needs no second idea of what an index is keyed on.
/// </summary>
public static class RepositoryKey
{
    private const string GitEntry = ".git";

    /// <summary>The key for a repository with no solution, given its root.</summary>
    public static string For(string repositoryRoot) => Path.Combine(repositoryRoot, GitEntry);

    /// <summary>True when a solution path is a repository key rather than a solution file.</summary>
    public static bool Is(string solutionPath) =>
        string.Equals(
            Path.GetFileName(solutionPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            GitEntry,
            StringComparison.Ordinal);

    /// <summary>The repository root a key names.</summary>
    public static string RootOf(string key) =>
        Path.GetDirectoryName(key.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? key;
}
