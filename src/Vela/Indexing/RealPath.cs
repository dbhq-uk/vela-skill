namespace Vela.Indexing;

/// <summary>
/// One spelling for one file, so that two ways of naming it are one identity.
///
/// <b>The bug this exists for.</b> vela keys a pending job on the absolute path of the
/// `.scip` it is waiting for, and `vela import` clears that key under the absolute path it
/// resolved the argument to. <see cref="Path.GetFullPath(string)"/> was doing both, and it
/// does not resolve symbolic links: it only removes '.', '..' and relative prefixes. So
/// the two sides disagreed whenever any directory above the repository was a link.
///
/// On macOS that is not an edge case, it is the default. <c>Path.GetTempPath()</c> returns
/// a path under <c>/var</c>, <c>/var</c> is a link to <c>/private/var</c>, and
/// <c>Directory.GetCurrentDirectory()</c> answers with the resolved form while a path
/// derived from the solution argument keeps the unresolved one. The job then never
/// settled: every answer printed "INCOMPLETE" naming an import the user had already run.
/// The same failure reproduces on Linux under a symlinked checkout, which is how this was
/// confirmed to be a defect in vela rather than a quirk of one runner.
///
/// <b>Why the roots too.</b> The same disagreement is worse on the way in. A foreign
/// indexer run inside a symlinked checkout writes the resolved root into project_root,
/// and vela rebases every document against its own unresolved root. GetRelativePath then
/// answers with '..', every document is reported as lying outside the repository, and a
/// whole language is dropped from the index with only the banner to show for it. Resolving
/// both roots is what makes that import land.
///
/// <b>Case.</b> Windows and macOS ask for a file case-insensitively but store the case it
/// was created with, so `C:\Repo` and `C:\repo` are one directory and two strings, and the
/// same pending job never settles for that reason alone. The true on-disk case is
/// therefore read back on those platforms, and a component spelled exactly as it is on
/// disk is never corrected to another one: a case-sensitive volume can be mounted on
/// macOS, `Repo` and `repo` are two directories there, and answering with the wrong one
/// would be worse than not correcting at all. On Linux the case is deliberately left
/// alone: `Foo.cs` and `foo.cs` really are two files there, and folding them would be a
/// lie.
///
/// <b>It never fails.</b> A component that is not there yet, or cannot be read, is kept
/// exactly as it was written. A path vela is about to create still has to have one
/// spelling, and a permission error on some directory above the repository is not a reason
/// to refuse to index it.
/// </summary>
public static class RealPath
{
    /// <summary>
    /// The path with '.', '..' and any relative prefix removed, every symbolic link on it
    /// followed, and on a case-insensitive platform every component spelled the way the
    /// filesystem spells it.
    /// </summary>
    public static string Of(string path)
    {
        // The signature already says a string, so a null is a caller's bug rather than a
        // path. Handing it back would put a null where every caller's own signature
        // promises there is a path, and it would surface as one much later, in a hash or a
        // database row, with nothing left pointing here. The empty string is not the same
        // thing: it is a value, and it is kept.
        ArgumentNullException.ThrowIfNull(path);

        return Of(path, hops: 0);
    }

    /// <summary>
    /// The runtime follows a chain of links for us and gives up at forty. This counts
    /// something it cannot: a link whose TARGET is itself reached through another link,
    /// which has to be resolved from its own root before it means anything. macOS makes
    /// that the ordinary case rather than the exotic one - a link created under
    /// Path.GetTempPath records a target beginning "/var", and "/var" is itself a link to
    /// "/private/var" - so a target taken at face value is only half resolved. The same
    /// bound is used for the same reason: to stop rather than loop.
    /// </summary>
    private const int MaxHops = 40;

    private static string Of(string path, int hops)
    {
        if (path.Length == 0) return path;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path this machine can resolve at all. Handing it back unchanged keeps
            // this a normalisation and leaves the complaining to whoever tries to open it.
            return path;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return full;

        var components = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var resolved = root;
        foreach (var component in components)
        {
            // Rebuilt from the resolved prefix rather than from the original, so a link
            // half way up is followed and everything below it is walked in the real tree.
            resolved = Path.Combine(resolved, OnDiskName(resolved, component));
            resolved = FollowLink(resolved, hops);
        }

        return resolved;
    }

    /// <summary>
    /// True when two paths name the same file, which on a case-insensitive filesystem is
    /// not the same question as whether two strings are equal.
    /// </summary>
    public static bool Same(string left, string right) =>
        string.Equals(Of(left), Of(right), Comparison);

    /// <summary>
    /// Linux is the platform whose default filesystem is case-sensitive; Windows and macOS
    /// are not. The same distinction <see cref="IndexPaths"/> draws, for the same reason.
    /// </summary>
    internal static StringComparison Comparison => OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The component as the filesystem spells it, on a filesystem that would have found it
    /// however it was spelled. The requested spelling is kept when the entry is not there,
    /// when the platform is case-sensitive, or when the name contains a character .NET's
    /// matcher treats as a wildcard - on Unix those are legal in a filename and a pattern
    /// could otherwise match some other file entirely.
    /// </summary>
    private static string OnDiskName(string directory, string component)
    {
        if (OperatingSystem.IsLinux()) return component;
        if (component.Contains('*') || component.Contains('?')) return component;

        try
        {
            var parent = new DirectoryInfo(directory);
            if (!parent.Exists) return component;

            return PickOnDiskName(component, parent.EnumerateFileSystemInfos(component).Select(e => e.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable, gone between the two calls, or not a name this platform can
            // enumerate. None of those is a reason to give up on the whole path.
        }

        return component;
    }

    /// <summary>
    /// Which of the names a directory offered is the one that was asked for.
    ///
    /// An exact match wins, and is looked for first. This is not pedantry: .NET matches
    /// with <c>MatchCasing.PlatformDefault</c>, which is case-insensitive on macOS
    /// whatever the volume underneath actually does, and a case-sensitive volume can
    /// perfectly well hold both `Repo` and `repo`. Taking the first case-insensitive match
    /// there would answer `/v/Repo/App.sln` for `/v/repo/App.sln`: a different directory,
    /// silently, with vela then indexing and answering about a tree nobody named. A name
    /// that is spelled exactly as asked is on disk exactly as asked, so nothing needs
    /// correcting; correction is for the case-insensitive volume, where at most one entry
    /// can match at all.
    ///
    /// When only case-insensitive matches exist, the ordinally least is taken rather than
    /// whichever the directory listed first, because the answer is hashed into an index
    /// cache name and must not depend on the order a directory happened to be read in.
    /// </summary>
    internal static string PickOnDiskName(string component, IEnumerable<string> candidates)
    {
        string? folded = null;
        foreach (var name in candidates)
        {
            if (string.Equals(name, component, StringComparison.Ordinal)) return name;

            if (string.Equals(name, component, StringComparison.OrdinalIgnoreCase) &&
                (folded is null || string.CompareOrdinal(name, folded) < 0))
                folded = name;
        }

        return folded ?? component;
    }

    /// <summary>
    /// The real path of a link's target, or the path itself when it is not a link. A chain
    /// of links is followed by the runtime; the target it lands on is then resolved from
    /// its own root here, because that target can perfectly well be reached through links
    /// of its own and the runtime does not look at them.
    /// </summary>
    private static string FollowLink(string path, int hops)
    {
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (entry.LinkTarget is null) return path;

            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null) return path;

            return hops >= MaxHops
                ? Path.GetFullPath(target.FullName)
                : Of(target.FullName, hops + 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A broken link, a link the process may not read, or a cycle the runtime gave
            // up on. The unresolved path is still the best name anybody has for it.
            return path;
        }
    }
}
