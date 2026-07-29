using System.IO.Enumeration;

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
/// was affected: it compares timestamps, and if anything under the root the index was
/// built against is newer than the index, it says so and lets the existing banner and
/// exit code do the rest. No file is opened and nothing is hashed, so the cost is a
/// directory walk.
///
/// That root is <see cref="ProjectRoot"/>, the same one ScipEmitter rooted the index at,
/// and it is passed in rather than worked out here so there is one definition of it. It
/// used to be the solution directory, which stopped matching the index the moment the
/// index widened to the repository root: in a `repo/src/App.sln` layout every file under
/// `repo/tests/` was in the index and none of it was watched, so editing a test left
/// every verb answering exit 0 at line numbers that had moved.
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
    /// <param name="projectRoot">
    /// The root the index was built against, from <see cref="ProjectRoot"/>. Every
    /// document in the index lives under it, and every file under it can become one, so
    /// this is the walk that watches exactly what the index covers and no more.
    /// </param>
    public static HealthRecord Check(HealthRecord health, string projectRoot, string? indexPath = null)
    {
        var root = string.IsNullOrEmpty(projectRoot) ? null : Path.GetFullPath(projectRoot);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return health;

        var indexDirectory = string.IsNullOrEmpty(indexPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(indexPath));

        var scan = Scan(root, health.BuiltAtUtc, indexDirectory);
        var (changedCount, newestPath, newestTime) = (scan.ChangedCount, scan.NewestPath, scan.NewestTime);

        if (changedCount == 0) return health;

        // Relative to the same root every path in an answer is relative to, so the file
        // named here can be handed straight back to outline.
        var relative = Path.GetRelativePath(root, newestPath!).Replace('\\', '/');

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
    /// Every entry the walk enumerates from a directory: its full path, whether it is
    /// itself a directory, and its bare name. Its only reason to exist is that
    /// <see cref="System.IO.Enumeration.FileSystemEnumerable{TResult}"/> hands the walk
    /// exactly this much for the cost of listing the directory and nothing more - unlike
    /// <see cref="Directory.EnumerateFileSystemEntries(string)"/> paired with
    /// <see cref="Directory.Exists(string)"/>, which is what this walk used to do and
    /// which stats every entry a second time just to learn what the first call already
    /// knew. On a real 375,608-line solution that second stat, paid on all 50,906 files
    /// under the project root, was the walk's entire cost.
    /// </summary>
    private readonly record struct Entry(string Path, bool IsDirectory, string Name);

    /// <summary>
    /// Counts the source files modified after <paramref name="builtAtUtc"/> and returns
    /// the newest of them, alongside how many files the walk had to examine to do it.
    ///
    /// The newest is reported rather than the first one found, so that the same tree and
    /// the same index always produce the same sentence regardless of the order the
    /// filesystem hands back directory entries (Constraint 1). Ties are broken on the
    /// path, ordinally, for the same reason.
    ///
    /// Public so a test can call it directly and assert <see cref="StalenessScan.FilesExamined"/>
    /// stays flat as the number of irrelevant files in the tree grows, which is the
    /// deterministic stand-in for a timing assertion that would be flaky on a shared
    /// machine. Only a file whose extension is one vela indexes is counted as examined:
    /// every other entry is settled from the directory listing itself, at no extra cost
    /// whatever else the tree also holds.
    /// </summary>
    public static StalenessScan Scan(string root, DateTime builtAtUtc, string? indexDirectory = null)
    {
        var count = 0;
        string? newestPath = null;
        var newestTime = DateTime.MinValue;
        var examined = 0;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<Entry> entries;
            try
            {
                entries = EnumerateEntries(directory);
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
                if (entry.IsDirectory)
                {
                    if (SkippedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (indexDirectory is not null &&
                        string.Equals(entry.Path, indexDirectory, StringComparison.Ordinal)) continue;

                    pending.Push(entry.Path);
                    continue;
                }

                var extension = Path.GetExtension(entry.Name);
                if (!SourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                examined++;

                DateTime modified;
                try
                {
                    modified = File.GetLastWriteTimeUtc(entry.Path);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (modified <= builtAtUtc) continue;

                count++;
                if (modified > newestTime ||
                    (modified == newestTime && string.CompareOrdinal(entry.Path, newestPath) < 0))
                {
                    newestTime = modified;
                    newestPath = entry.Path;
                }
            }
        }

        return new StalenessScan(count, newestPath, newestTime, examined);
    }

    /// <summary>
    /// One directory's entries, each read once. The low-level enumerator is what makes
    /// that true: <see cref="FileSystemEntry.IsDirectory"/> comes from the directory
    /// listing the operating system already returned, not from a second call, which is
    /// the whole of what this walk needed to stop costing a stat per file.
    /// </summary>
    private static IEnumerable<Entry> EnumerateEntries(string directory) =>
        new FileSystemEnumerable<Entry>(
            directory,
            (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.IsDirectory, e.FileName.ToString()),
            new EnumerationOptions { RecurseSubdirectories = false });
}

/// <summary>
/// What one walk of the source tree found: how many files had changed since the index
/// was built, the most recently changed of them, and how many files the walk had to
/// examine to answer that.
///
/// <see cref="FilesExamined"/> exists so a test can pin the walk's cost without timing
/// it: on a real 375,608-line solution the unbounded version of this walk stated all
/// 50,906 files under the project root on every invocation, and a wall-clock assertion
/// of that would be flaky on a shared machine. A count is not.
/// </summary>
public readonly record struct StalenessScan(int ChangedCount, string? NewestPath, DateTime NewestTime, int FilesExamined);
