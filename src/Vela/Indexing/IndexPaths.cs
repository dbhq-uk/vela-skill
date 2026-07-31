using System.Security.Cryptography;
using System.Text;

namespace Vela.Indexing;

public static class IndexPaths
{
    /// <summary>
    /// Indexes live in the user cache directory, keyed by solution path.
    /// Constraint 2/3: never write into the repository being indexed.
    ///
    /// This is pure path resolution: it does not touch the filesystem, except that
    /// it throws rather than returning a path if the resolved cache directory would
    /// land inside the solution's own directory tree (see the guard below). Callers
    /// that are about to write to the returned path (for example the `index` verb)
    /// must call <see cref="EnsureDirectoryExists"/> first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the resolved cache directory is inside the directory tree of the
    /// solution being indexed. This can happen if XDG_CACHE_HOME (or, on the
    /// fallback path, the user profile) has been pointed at or under the repository.
    /// Vela never falls back silently in this case: writing the index into the
    /// repository it is indexing would violate Constraint 2, so this is a loud
    /// failure rather than a quiet, surprising one.
    /// </exception>
    public static string ForSolution(string solutionPath)
    {
        // RealPath rather than Path.GetFullPath, because this hash is the ONLY thing that
        // makes `vela index` and `vela refs` talk about the same database, and the two
        // verbs need not have been given the same spelling of the same solution. Only the
        // index verb resolves its argument any further, so a query naming the solution
        // through a symbolic link - or, on Windows and macOS, with a different letter case
        // - hashed to a different name and was told "No index for ...", with the fix being
        // the command the user had just run. GetFullPath cannot answer that: it removes
        // '.', '..' and a relative prefix and stops.
        var full = RealPath.Of(solutionPath);
        var solutionDir = Path.GetFullPath(Path.GetDirectoryName(full) ?? full);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(full);

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

        var dir = Path.GetFullPath(Path.Combine(cache, "vela"));

        if (IsWithin(dir, solutionDir))
        {
            throw new InvalidOperationException(
                $"The resolved index cache directory '{dir}' is inside the solution directory " +
                $"'{solutionDir}'. Indexing must never write into the repository being indexed " +
                "(Constraint 2). Check the XDG_CACHE_HOME environment variable: it is set to a " +
                "path inside this repository (or unset, with the user profile's .cache directory " +
                "itself inside the repository), and must instead point somewhere outside it.");
        }

        return Path.Combine(dir, $"{name}-{hash}.db");
    }

    /// <summary>
    /// True if <paramref name="candidate"/> is <paramref name="root"/> itself, or is
    /// nested anywhere under it. Both paths must already be resolved with
    /// <see cref="Path.GetFullPath(string)"/>. The comparison is ordinal, using
    /// case-insensitive matching only on platforms whose default filesystem is
    /// case-insensitive, so that a directory which merely shares a string prefix
    /// with root - for example "/repo-backup" against "/repo" - is never mistaken
    /// for a directory nested inside it.
    /// </summary>
    private static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidate, trimmedRoot, comparison))
            return true;

        var prefix = trimmedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Creates the cache directory that holds the given index path, if it does not
    /// already exist. Path resolution itself must stay side-effect free, so callers
    /// that are about to open or create the database file call this first.
    /// </summary>
    public static void EnsureDirectoryExists(string indexPath)
    {
        var dir = Path.GetDirectoryName(indexPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
