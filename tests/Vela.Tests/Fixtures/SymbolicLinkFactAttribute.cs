using Xunit;

namespace Vela.Tests.Fixtures;

/// <summary>
/// A fact whose SETUP needs a symbolic link, which an unprivileged process cannot create
/// on Windows.
///
/// The behaviour under test is not Unix-only, and that distinction matters: a repository
/// reached through a link breaks path identity on every platform, and Windows has its own
/// ways of arriving at one (a junction, a mapped drive, a subst). What is Unix-only is
/// being able to MAKE one from a test without Developer Mode or an elevated process, so
/// the test is skipped there with the reason said out loud rather than passing quietly.
/// macOS runs it, and macOS is where this failure was found: its temp directory is reached
/// through /var, which is a link to /private/var.
/// </summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Creating a symbolic link on Windows needs Developer Mode or elevation.";
    }
}
