namespace Vela.Tests.Fixtures;

/// <summary>
/// An absolute path that stands in for somewhere on disk without ever being on disk,
/// spelled the way the platform running the test spells one.
///
/// <b>Why it exists.</b> A .scip index declares its project_root as a URI-encoded
/// absolute path, and every relative_path in it is resolved against that root, so a test
/// standing in for a foreign indexer has to name a root. Written by hand as "/repo" that
/// names a directory on Unix and nothing at all on Windows: <c>new Uri("/repo/")</c>
/// throws UriFormatException there, which is what failed twenty-four of these tests on
/// windows-latest. It is a defect in the tests rather than in vela - the importer itself
/// never constructs a Uri from a hand-written path.
///
/// <b>Why one volume.</b> On Windows a rebase across volumes is legitimately impossible,
/// so <see cref="Path.GetRelativePath"/> hands the absolute path straight back and the
/// importer correctly reports the document as outside its root. A test that means "these
/// two directories are one tree" therefore has to put them on the same volume, which is
/// what the shared prefix here guarantees. Nothing is created, read or written under
/// these paths on any platform, so the drive letter is arbitrary.
/// </summary>
internal static class Synthetic
{
    private static readonly string Prefix = OperatingSystem.IsWindows() ? "C:" + Path.DirectorySeparatorChar : "/";

    /// <summary>
    /// An absolute directory path, built from '/'-separated segments so the call sites
    /// read the same on every platform.
    /// </summary>
    public static string Root(string segments) =>
        Prefix + segments.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// The same directory as the absolute file URI a .scip declares in project_root. The
    /// trailing separator is what makes Uri treat it as a directory rather than a file.
    /// </summary>
    public static string RootUri(string segments)
    {
        var directory = Root(segments);
        if (!directory.EndsWith(Path.DirectorySeparatorChar)) directory += Path.DirectorySeparatorChar;
        return new Uri(directory).AbsoluteUri;
    }

    /// <summary>
    /// The same directory as vela would print it: separators normalised to '/', which is
    /// what every path vela records uses on every platform.
    /// </summary>
    public static string Printed(string segments) => Root(segments).Replace('\\', '/');
}
