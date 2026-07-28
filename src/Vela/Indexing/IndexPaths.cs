using System.Security.Cryptography;
using System.Text;

namespace Vela.Indexing;

public static class IndexPaths
{
    /// <summary>
    /// Indexes live in the user cache directory, keyed by solution path.
    /// Constraint 3: never write into the repository being indexed.
    ///
    /// This is pure path resolution: it does not touch the filesystem. Callers
    /// that are about to write to the returned path (for example the `index`
    /// verb) must call <see cref="EnsureDirectoryExists"/> first.
    /// </summary>
    public static string ForSolution(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(full);

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

        var dir = Path.Combine(cache, "vela");
        return Path.Combine(dir, $"{name}-{hash}.db");
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
