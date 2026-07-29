namespace Vela.Indexing;

/// <summary>
/// Whether the source tree has moved on since the index was built.
///
/// vela builds an index once and then answers from a file. Nothing invalidated that
/// file, so after any edit every verb kept answering at exit 0 with no banner, at line
/// numbers that had moved, and the skill's instruction to treat a stale index as
/// incomplete described a signal that could never fire. Under Constraint 3 that is the
/// same failure as a project that did not load: the answer looks complete and is not.
///
/// This is the cheap, honest mitigation and nothing more. It is not incremental
/// reindex, and it does not try to work out whether the specific symbol you asked about
/// was affected: it compares timestamps, and if anything under the solution directory is
/// newer than the index, it says so and lets the existing banner and exit code do the
/// rest. No file is opened and nothing is hashed, so the cost is a directory walk.
/// </summary>
public static class Staleness
{
    /// <summary>
    /// What counts as source for this purpose: the files whose contents the index is
    /// derived from, plus the project and solution files that decide which of them are
    /// compiled at all. Anything else can change without changing what the compiler saw.
    /// </summary>
    private static readonly string[] SourceExtensions =
    {
        ".cs", ".vb", ".cshtml", ".razor", ".csproj", ".vbproj", ".sln", ".slnx",
        ".props", ".targets"
    };

    /// <summary>
    /// Directories never walked. Build output changes on every build and is not what
    /// the index describes, and treating it as an edit would leave every query
    /// permanently degraded, which is a warning nobody reads. .git changes on every
    /// command that touches the repository, for the same reason.
    /// </summary>
    private static readonly string[] SkippedDirectories =
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules"
    };

    /// <summary>
    /// The health record, degraded if the tree is newer than the index. Any existing
    /// degradation is kept: staleness is an additional reason, never a replacement.
    /// </summary>
    public static HealthRecord Check(HealthRecord health, string solutionPath, string? indexPath = null)
    {
        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        if (string.IsNullOrEmpty(solutionDirectory) || !Directory.Exists(solutionDirectory))
            return health;

        var indexDirectory = string.IsNullOrEmpty(indexPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(indexPath));

        var (changedCount, newestPath, newestTime) =
            NewestSourceChangeAfter(solutionDirectory, health.BuiltAtUtc, indexDirectory);

        if (changedCount == 0) return health;

        var relative = Path.GetRelativePath(solutionDirectory, newestPath!).Replace('\\', '/');

        var detail =
            $"stale index: {changedCount} source file(s) changed after the index was built at "
            + $"{health.BuiltAtUtc:u}, most recently '{relative}' at {newestTime:u}. Line numbers and "
            + "references in this answer describe the code as it was, not as it is. Run vela index.";

        return health with
        {
            Degraded = true,
            Detail = string.IsNullOrEmpty(health.Detail) ? detail : health.Detail + "; " + detail
        };
    }

    /// <summary>
    /// Counts the source files modified after <paramref name="builtAtUtc"/> and returns
    /// the newest of them.
    ///
    /// The newest is reported rather than the first one found, so that the same tree and
    /// the same index always produce the same sentence regardless of the order the
    /// filesystem hands back directory entries (Constraint 1). Ties are broken on the
    /// path, ordinally, for the same reason.
    /// </summary>
    private static (int Count, string? Path, DateTime Time) NewestSourceChangeAfter(
        string root, DateTime builtAtUtc, string? indexDirectory)
    {
        var count = 0;
        string? newestPath = null;
        var newestTime = DateTime.MinValue;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A directory vela cannot read tells us nothing either way. It is not
                // evidence the index is fresh, but it is also not a reason to declare
                // it stale, and the walk must not throw on the query path.
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (indexDirectory is not null &&
                        string.Equals(Path.GetFullPath(entry), indexDirectory, StringComparison.Ordinal)) continue;

                    pending.Push(entry);
                    continue;
                }

                var extension = Path.GetExtension(entry);
                if (!SourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                DateTime modified;
                try
                {
                    modified = File.GetLastWriteTimeUtc(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (modified <= builtAtUtc) continue;

                count++;
                if (modified > newestTime ||
                    (modified == newestTime && string.CompareOrdinal(entry, newestPath) < 0))
                {
                    newestTime = modified;
                    newestPath = entry;
                }
            }
        }

        return (count, newestPath, newestTime);
    }
}
