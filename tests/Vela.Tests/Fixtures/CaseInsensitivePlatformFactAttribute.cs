using Xunit;

namespace Vela.Tests.Fixtures;

/// <summary>
/// A fact about the platforms that ask for a file case-insensitively, which is Windows and
/// macOS and not Linux.
///
/// Case correction is the half of path identity that a symbolic link cannot stand in for:
/// `C:\Repo\App.sln` and `C:\repo\App.sln` are one file and two strings, and every key vela
/// holds - the index cache name, a pending job, an import's health record - is only an
/// identity if the two produce one string. On Linux those really are two files, so the
/// correction is deliberately not done there and the fact is skipped rather than inverted:
/// asserting the opposite would pin nothing on the platforms this is about.
/// </summary>
public sealed class CaseInsensitivePlatformFactAttribute : FactAttribute
{
    public CaseInsensitivePlatformFactAttribute()
    {
        if (OperatingSystem.IsLinux())
            Skip = "Linux asks for a file case-sensitively: two spellings really are two files there.";
    }
}
