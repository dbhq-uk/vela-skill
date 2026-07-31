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
/// therefore read back on those platforms. On Linux it is deliberately not: `Foo.cs` and
/// `foo.cs` really are two files there, and folding them would be a lie.
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
        if (string.IsNullOrEmpty(path)) return path;

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
            resolved = FollowLink(resolved);
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

            foreach (var entry in parent.EnumerateFileSystemInfos(component))
            {
                // The pattern is the name itself, so at most one entry can match on a
                // case-insensitive filesystem, and it is checked rather than assumed.
                if (string.Equals(entry.Name, component, StringComparison.OrdinalIgnoreCase))
                    return entry.Name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable, gone between the two calls, or not a name this platform can
            // enumerate. None of those is a reason to give up on the whole path.
        }

        return component;
    }

    /// <summary>
    /// The final target of a link, or the path itself when it is not one. Chains are
    /// followed by the runtime, which stops at forty hops rather than looping forever.
    /// </summary>
    private static string FollowLink(string path)
    {
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (entry.LinkTarget is null) return path;

            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? path : Path.GetFullPath(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A broken link, a link the process may not read, or a cycle the runtime gave
            // up on. The unresolved path is still the best name anybody has for it.
            return path;
        }
    }
}
