using Xunit;

namespace Vela.Tests.Fixtures;

/// <summary>
/// A fact that only has a meaning on a Unix-like platform, and is skipped elsewhere with
/// the reason stated rather than silently passing.
///
/// The one test that needs this makes a directory unreadable to prove the staleness walk
/// degrades rather than reporting a tree it could not finish walking as fresh. Windows
/// has no equivalent of chmod that <see cref="System.IO.File.SetUnixFileMode"/> exposes,
/// so the test cannot be run there. It is skipped on Windows and runs everywhere else,
/// which is what keeps the assertion honest on the platforms that can make it.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Requires Unix file modes, which Windows does not have.";
    }
}
