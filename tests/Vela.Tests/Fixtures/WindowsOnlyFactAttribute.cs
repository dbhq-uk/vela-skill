using Xunit;

namespace Vela.Tests.Fixtures;

/// <summary>
/// A fact that only has a meaning on Windows, and is skipped elsewhere with the reason
/// stated rather than silently passing.
///
/// The one test that needs this holds a handle on the index while a rebuild tries to move
/// a new one over it. Windows refuses that rename and Unix allows it, so the failure the
/// test is about cannot be produced anywhere else. The property is still covered on every
/// platform by the test beside it, which reaches the same code path by putting a directory
/// where the index goes; this one proves the real cause on the one platform that has it.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Only Windows refuses to rename over a file another process has open.";
    }
}
