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
/// was affected: it compares timestamps, and if a watched file under the root the index
/// was built against is newer than the index, it says so and lets the existing banner
/// and exit code do the rest. No file is opened and nothing is hashed, so the cost is a
/// directory walk.
///
/// That root is <see cref="ProjectRoot"/>, the same one ScipEmitter rooted the index at,
/// and it is passed in rather than worked out here so there is one definition of it. It
/// used to be the solution directory, which stopped matching the index the moment the
/// index widened to the repository root: in a `repo/src/App.sln` layout every file under
/// `repo/tests/` was in the index and none of it was watched, so editing a test left
/// every verb answering exit 0 at line numbers that had moved.
///
/// <b>What the shared root does and does not guarantee.</b> It guarantees the ROOT: no
/// document can be indexed from outside the tree this walk covers, which is the failure
/// above. It does not guarantee the SET. <see cref="SourceExtensions"/> and
/// <see cref="SkippedDirectories"/> make the watched files a proper subset of the
/// indexed ones - on the real solution 365 indexed documents sit under `bin` or `obj`
/// alone, and a generated file the compiler was handed from anywhere else with an
/// extension not on that list is indexed and unwatched too. Those files can change
/// without degrading anything. The exclusions are deliberate and are the price of a
/// walk cheap enough to run on every query; what is not acceptable is a reader
/// believing otherwise, so README.md and SKILL.md both say outright that the absence of
/// a banner is not proof the tree is unchanged.
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
    /// The health record, degraded if a watched file is newer than the index, and also
    /// degraded if the check could not be made at all because the root is not there.
    /// Any existing degradation is kept: staleness is an additional reason, never a
    /// replacement.
    /// </summary>
    /// <param name="projectRoot">
    /// The root the index was built against, from <see cref="ProjectRoot"/>. Every
    /// document in the index lives under it, so nothing is indexed from outside the
    /// tree this walks.
    /// </param>
    /// <param name="indexedFiles">
    /// The files the index was built FROM, relative to the root, from
    /// <see cref="ProjectInputs.ReadDocumentInputs"/>. Null means the index records none,
    /// which is what an index built by `vela import` alone looks like, and the deletion
    /// check simply does not run.
    /// </param>
    public static HealthRecord Check(
        HealthRecord health,
        string projectRoot,
        string? indexPath = null,
        IReadOnlyList<string>? indexedFiles = null)
    {
        var root = string.IsNullOrEmpty(projectRoot) ? null : Path.GetFullPath(projectRoot);

        // A root that is not there is not evidence of freshness. It used to be treated
        // as one: the method returned the record untouched, so every verb answered at
        // exit 0 with no banner, which is a freshness check that did not run reported
        // as a freshness check that passed. The root is not stored in the index; it is
        // worked out again on every query by walking up for a `.git` entry, so a moved
        // or renamed repository, a removed linked worktree, or a `.git` file whose
        // gitdir no longer resolves can all land here while the index itself is
        // perfectly readable.
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            var named = string.IsNullOrEmpty(root)
                ? "no root could be worked out for it"
                : $"'{root}' is not there";

            return Degrade(health,
                $"index freshness could not be checked: the root the index was built against, {named}. "
                + "That root is worked out on each query by walking up for a .git entry, so the "
                + "repository has most likely moved, been renamed or been removed since the index was "
                + "built. Nothing in this answer has been compared against the code on disk. Run vela "
                + "index from the solution's current location.");
        }

        var indexDirectory = string.IsNullOrEmpty(indexPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(indexPath));

        var scan = Scan(root, health.BuiltAtUtc, indexDirectory);
        var (changedCount, newestPath, newestTime) = (scan.ChangedCount, scan.NewestPath, scan.NewestTime);

        // A directory the walk could not list may hold a file newer than the index, so a
        // clean result from an incomplete walk is not evidence of freshness. Reported
        // whether or not anything else was found to have changed.
        if (scan.UnreadableDirectories > 0)
        {
            health = Degrade(health,
                $"index freshness could only be partly checked: {scan.UnreadableDirectories} "
                + "directory(ies) under the project root could not be read, so any source file in "
                + "them has not been compared against the index.");
        }

        // Deletions and renames, which the walk above cannot see at all: it stats the files
        // that are THERE, so it can only ever notice something newer than the index. A file
        // that has gone leaves the index naming a path nobody can open, and an agent handed
        // that path will try to open it, which is a wrong answer of the worst kind. A
        // rename is a deletion plus an addition, and moving a file keeps its modification
        // time, so the mtime walk cannot see either half of one: noticing the deletion is
        // what notices the rename.
        //
        // Reported alongside a change rather than instead of one. Two things being wrong
        // with an index is two facts, and each of them names a different file to look at.
        if (indexedFiles is { Count: > 0 })
        {
            var missing = ScanForMissing(root, indexedFiles);

            if (missing.MissingCount > 0)
            {
                health = Degrade(health,
                    $"stale index: {missing.MissingCount} file(s) the index was built from are no longer "
                    + $"on disk, the first of them '{missing.FirstMissing}'. Answers may name files that "
                    + "cannot be opened, and code that has moved is recorded under the path it moved from. "
                    + "Run vela index.");
            }
        }

        if (changedCount == 0) return health;

        // Relative to the same root every path in an answer is relative to, so the file
        // named here can be handed straight back to outline.
        var relative = Path.GetRelativePath(root, newestPath!).Replace('\\', '/');

        return Degrade(health,
            $"stale index: {changedCount} source file(s) changed after the index was built at "
            + $"{health.BuiltAtUtc:u}, most recently '{relative}' at {newestTime:u}. Line numbers and "
            + "references in this answer describe the code as it was, not as it is. Run vela index.");
    }

    /// <summary>
    /// How many of the files the index was built from are no longer on disk, and the first
    /// of them.
    ///
    /// <b>Bounded to exactly the set the walk above watches, and deliberately so.</b> A
    /// recorded input is checked only when its extension is one vela indexes and no part of
    /// its path is a directory the walk skips. That makes the two halves of the freshness
    /// check answer about one set of files rather than two, and it keeps `dotnet clean` from
    /// degrading every query forever: on a real solution 365 indexed documents sit under
    /// `bin` or `obj`, which are regenerated on their own schedule and are not what the
    /// index describes. A banner that fires on an ordinary command is a banner nobody reads.
    ///
    /// <b>Cost, measured rather than estimated.</b> One <see cref="File.Exists"/> per
    /// checked file and nothing else: no file is opened and nothing is hashed. On a
    /// generated 2,500-file solution, twelve `vela def` runs each, the median query went
    /// from 0.225s to 0.275s, and the check itself accounts for 29ms of that - 6.7ms to
    /// read the 2,502 ledger rows and 22ms to ask the filesystem about all 2,500 of them.
    /// It scales with the number of files the index was built from and with nothing else.
    ///
    /// The first missing file is the ordinally least, rather than the first one found, so
    /// the same tree and the same index produce the same sentence however the rows arrived
    /// (Constraint 1).
    ///
    /// Public so a test can assert <see cref="MissingScan.FilesChecked"/> directly, which is
    /// the deterministic stand-in for a timing assertion that would be flaky on a shared
    /// machine.
    /// </summary>
    public static MissingScan ScanForMissing(string root, IReadOnlyList<string> indexedFiles)
    {
        var full = Path.GetFullPath(root);
        var count = 0;
        string? first = null;
        var checkedFiles = 0;

        foreach (var relative in indexedFiles)
        {
            if (!IsWatched(relative)) continue;

            checkedFiles++;

            // Combined and not normalised. The ledger writes '/' on every platform and
            // every operating system API accepts it, the result is used for one question
            // and then discarded, and the path REPORTED is the relative one, which is the
            // form every other path in an answer is written in.
            //
            // File.Exists rather than a comparison against the walk above, which visits
            // these same files and could have been made to hand back a set for nothing.
            // The filesystem is asked the question directly because it is the only thing
            // that knows its own rules: a set comparison would have to decide for itself
            // whether Foo.cs and foo.cs are one file, which is true on Windows and macOS
            // and false on Linux, and getting that wrong means naming a file as missing
            // that is sitting right there. A false gap on every query is the crying-wolf
            // failure this whole check exists to be worth listening to.
            if (File.Exists(Path.Combine(full, relative))) continue;

            count++;
            if (first is null || string.CompareOrdinal(relative, first) < 0) first = relative;
        }

        return new MissingScan(count, first, checkedFiles);
    }

    /// <summary>
    /// Whether a recorded input is one this check is entitled to have an opinion about:
    /// an extension vela indexes, under no directory the walk skips.
    ///
    /// The path is the one stored in the ledger, which is relative to the root and written
    /// with '/' on every platform, so it is split on both separators rather than on the
    /// platform's own: a ledger written on Windows is read on Linux by anybody who shares a
    /// checkout, and a rule that only worked on one of them would be a rule that silently
    /// stopped applying.
    /// </summary>
    private static bool IsWatched(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return false;
        if (!SourceExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            return false;

        var segments = relative.Split('/', '\\');

        // The last segment is the file name, and a file called `bin` is not a directory
        // called `bin`.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (SkippedDirectories.Contains(segments[i], StringComparer.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>
    /// The record with one more reason on it. Any existing degradation is kept and the
    /// new reason is appended: a build-time failure and an out-of-date tree are two
    /// different things wrong with one answer, and replacing either with the other
    /// would hide it.
    /// </summary>
    private static HealthRecord Degrade(HealthRecord health, string detail) =>
        health with
        {
            Degraded = true,
            Detail = string.IsNullOrEmpty(health.Detail) ? detail : health.Detail + "; " + detail
        };

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
        var unreadable = 0;

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
                // A directory vela cannot read tells us nothing either way, and that is
                // exactly why it is counted. Passing over it in silence would report a
                // check that did not run as a check that passed. The walk still must not
                // throw on the query path, so the caller degrades on the count instead.
                unreadable++;
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

        return new StalenessScan(count, newestPath, newestTime, examined, unreadable);
    }

    /// <summary>
    /// One directory's entries, each read once. The low-level enumerator is what makes
    /// that true: <see cref="FileSystemEntry.IsDirectory"/> comes from the directory
    /// listing the operating system already returned, not from a second call, which is
    /// the whole of what this walk needed to stop costing a stat per file.
    ///
    /// Both options are set deliberately, and neither is the default.
    ///
    /// <see cref="EnumerationOptions.AttributesToSkip"/> defaults to Hidden | System,
    /// where the <see cref="Directory.EnumerateFileSystemEntries(string)"/> this
    /// replaced asks for <see cref="EnumerationOptions.Compatible"/> and so skips
    /// nothing. Leaving the default in place drops every dot-prefixed name on Linux and
    /// macOS, and anything marked hidden on Windows, so a source file under a directory
    /// like `.generated` would never be examined and an edit to it would leave every
    /// verb answering exit 0 with no banner. What this walk ignores is the skip list
    /// above, which is written down and explained; a silent second filter that nothing
    /// documents is the Constraint 3 failure the whole check exists to prevent.
    ///
    /// <see cref="EnumerationOptions.IgnoreInaccessible"/> is FALSE, which is the one
    /// place this walk deliberately prefers throwing to carrying on. Left true, the
    /// enumerator swallows a directory it cannot open and yields nothing for it, so the
    /// walk cannot tell "no source files in there" from "could not look", and reports a
    /// tree it never finished reading as unchanged. Throwing hands that directory to the
    /// caller's catch, which counts it, and the count degrades the answer. The walk
    /// still never throws out of <see cref="Scan"/>: the catch is per directory, so one
    /// unreadable corner costs its own subtree and nothing else.
    /// </summary>
    private static IEnumerable<Entry> EnumerateEntries(string directory) =>
        new FileSystemEnumerable<Entry>(
            directory,
            (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.IsDirectory, e.FileName.ToString()),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false
            });
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
/// <summary>
/// What one walk of the tree found. <see cref="UnreadableDirectories"/> is how many
/// directories the walk could not list: those are not evidence the index is fresh, so
/// they are counted rather than passed over in silence, and the caller degrades on them.
/// </summary>
public readonly record struct StalenessScan(
    int ChangedCount,
    string? NewestPath,
    DateTime NewestTime,
    int FilesExamined,
    int UnreadableDirectories = 0);

/// <summary>
/// What one pass over the files the index was built from found: how many of them have gone,
/// the ordinally first of those, and how many the pass had to look for on disk.
///
/// <see cref="FilesChecked"/> is the cost, pinned by a test rather than timed: the check
/// runs on every query, and the thing that would make it expensive is checking files the
/// freshness walk never watched in the first place. A count is not flaky on a shared
/// machine; a wall clock is.
/// </summary>
public readonly record struct MissingScan(int MissingCount, string? FirstMissing, int FilesChecked);
