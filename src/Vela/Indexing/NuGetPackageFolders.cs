using System.Xml;
using System.Xml.Linq;

namespace Vela.Indexing;

/// <summary>
/// Where restored NuGet packages live on this machine, according to nuget.config.
///
/// vela classifies a file it cannot place under project_root as either somebody else's -
/// a package, or the .NET installation - or as a gap in the repository being indexed. The
/// second degrades the index and puts an INCOMPLETE banner above every answer. That
/// classification read NUGET_PACKAGES, fell back to ~/.nuget/packages, and stopped there,
/// so a repository that moves its package cache with
/// <c>&lt;add key="globalPackagesFolder" /&gt;</c> - the documented, supported way to do
/// it - had every package file called a gap in its own code. The index was permanently
/// degraded, the banner was permanently wrong, and a banner that is wrong is a banner
/// nobody reads by the time it is right. This project has paid for that twice.
///
/// <b>Parsed here rather than asked of the tooling.</b> Constraint 1 requires the answer
/// to follow from what is on disk, so nothing shells out to `dotnet` or `nuget`: that
/// would put a process launch, a machine's SDK resolution and a PATH lookup between a
/// query and its answer, and give a different result on a machine where the tool is
/// missing.
///
/// <b>What of the documented chain this covers.</b> NuGet merges, in this order, with the
/// nearest file winning:
///
///   1. machine-wide configuration - every *.config under the platform's machine
///      directory, in name order;
///   2. user-level configuration - the single NuGet.Config in the user's settings
///      directory;
///   3. every nuget.config from the filesystem root down to the starting directory.
///
/// All three are covered. What is NOT covered, deliberately, and what a reader should
/// know they are not getting:
///
///   - a nuget.config BELOW the starting directory. The walk starts at the solution's
///     directory, so a per-project file in a subdirectory is not read. Adding it would
///     mean one classification per project rather than one per index.
///   - `%VAR%` expansion inside a value. NuGet expands environment variables in config
///     values; vela does not, so a folder written that way falls back to the default and
///     stays loud rather than being resolved wrongly.
///   - `repositoryPath`, which is the packages.config-era setting and names a per-solution
///     packages directory rather than a global cache.
///   - `--configfile`, which is a command-line argument to NuGet and not a fact on disk.
///
/// Every one of those omissions fails in the recoverable direction: a folder vela does not
/// resolve is a folder whose files stay loud, which is a false gap somebody can see and
/// report, rather than first-party code silently dropped.
/// </summary>
/// <param name="GlobalPackagesFolder">
/// The single folder restore would write to, resolved in NuGet's documented precedence:
/// NUGET_PACKAGES, then the nearest configured globalPackagesFolder, then
/// ~/.nuget/packages.
/// </param>
/// <param name="FallbackPackageFolders">
/// The read-only folders restore also looks in, in the order the merged configuration
/// gives them. Additive down the chain; <c>&lt;clear /&gt;</c> empties what came before.
/// </param>
public sealed record NuGetPackageFolders(
    string GlobalPackagesFolder,
    IReadOnlyList<string> FallbackPackageFolders)
{
    /// <summary>
    /// Every folder on this machine that could hold a restored package, which is the only
    /// question vela actually asks of any of this.
    ///
    /// <b>Deliberately not the same list as NuGet's precedence produces.</b> That
    /// precedence answers "which folder will the next restore write to", and it is a
    /// single winner: a configured globalPackagesFolder REPLACES ~/.nuget/packages. It
    /// does not empty ~/.nuget/packages of the packages already in it, and a source file
    /// contributed from there is still nobody's first-party code. Narrowing to the winner
    /// would therefore have swapped one crying-wolf banner for another, on the machines
    /// where both directories hold packages, which is most of them. So the winner, the
    /// documented default and every fallback folder are all treated as package cache.
    ///
    /// Widening cannot lose first-party code the way guessing wider on the OTHER axis
    /// would: this list is consulted only for a file that is already outside project_root,
    /// and every entry in it is a directory somebody's configuration named as a package
    /// folder.
    ///
    /// Distinct and ordered, so the same machine and the same configuration classify the
    /// same way on every run (Constraint 1).
    /// </summary>
    public IReadOnlyList<string> EveryPackageFolder { get; } =
        new[] { GlobalPackagesFolder, DefaultGlobalPackagesFolder() }
            .Concat(FallbackPackageFolders)
            .Where(folder => !string.IsNullOrEmpty(folder))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(folder => folder, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The folders configured for a directory, merged the way NuGet documents.
    /// </summary>
    /// <param name="startDirectory">
    /// Where the walk up begins: the solution's own directory. It need not exist.
    /// </param>
    /// <param name="ambientConfigFiles">
    /// The machine-wide and user-level files, root-most first. Null means read the real
    /// ones for this platform; a list means use exactly these. Passed in rather than
    /// always read so the test suite can pin the merge without depending on what the
    /// machine running it happens to have in its own NuGet configuration (Constraint 1).
    /// </param>
    public static NuGetPackageFolders Resolve(
        string startDirectory, IReadOnlyList<string>? ambientConfigFiles = null)
    {
        var files = new List<string>(ambientConfigFiles ?? AmbientConfigFiles());
        files.AddRange(DirectoryChainConfigFiles(startDirectory));

        string? configured = null;
        var fallbacks = new List<string>();

        foreach (var file in files)
        {
            var document = Parse(file);
            if (document is null) continue;

            var directory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory();

            // The config section is last-one-wins, within a file and across files: a
            // nearer file simply overwrites the key. <clear /> inside it drops what came
            // before, which for a single key means the same thing, and is honoured so
            // that a file which clears and then says nothing leaves nothing behind.
            foreach (var entry in Section(document, "config"))
            {
                if (entry.Clear) configured = null;
                else if (string.Equals(entry.Key, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(entry.Value))
                {
                    configured = Absolute(directory, entry.Value);
                }
            }

            // fallbackPackageFolders is a list rather than a key, so it accumulates down
            // the chain and only <clear /> empties it.
            foreach (var entry in Section(document, "fallbackPackageFolders"))
            {
                if (entry.Clear) fallbacks.Clear();
                else if (!string.IsNullOrWhiteSpace(entry.Value)) fallbacks.Add(Absolute(directory, entry.Value));
            }
        }

        // NUGET_PACKAGES beats every configuration file, which is what NuGet documents and
        // what a CI runner setting it depends on.
        var environment = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        var global = !string.IsNullOrWhiteSpace(environment)
            ? Path.GetFullPath(environment)
            : configured ?? DefaultGlobalPackagesFolder();

        return new NuGetPackageFolders(
            global, fallbacks.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>~/.nuget/packages, or "" on a host with no user profile at all.</summary>
    private static string DefaultGlobalPackagesFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".nuget", "packages");
    }

    /// <summary>
    /// A configured value made absolute. NuGet resolves a relative folder against the
    /// directory of the file that declared it, which is the only reading that lets a
    /// repository commit `value="packages"` and have it mean the same thing in every
    /// checkout.
    /// </summary>
    private static string Absolute(string configDirectory, string value) =>
        Path.GetFullPath(Path.Combine(configDirectory, value.Trim()));

    /// <summary>One entry of a settings section: an add, or the clear that empties it.</summary>
    private readonly record struct Entry(bool Clear, string Key, string Value);

    /// <summary>
    /// The add and clear elements of one section, in document order. Element and attribute
    /// names are matched case-insensitively, because NuGet does and because a file written
    /// by hand is as likely to say fallbackpackagefolders as fallbackPackageFolders.
    /// </summary>
    private static IEnumerable<Entry> Section(XDocument document, string section)
    {
        var configuration = document.Root;
        if (configuration is null) yield break;

        foreach (var element in configuration.Elements())
        {
            if (!string.Equals(element.Name.LocalName, section, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var child in element.Elements())
            {
                if (string.Equals(child.Name.LocalName, "clear", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new Entry(Clear: true, "", "");
                    continue;
                }

                if (!string.Equals(child.Name.LocalName, "add", StringComparison.OrdinalIgnoreCase)) continue;

                yield return new Entry(
                    Clear: false,
                    Attribute(child, "key"),
                    Attribute(child, "value"));
            }
        }
    }

    private static string Attribute(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? "";

    /// <summary>
    /// One configuration file, or null when it is not there, will not open, or is not
    /// well-formed XML.
    ///
    /// A file that cannot be read yields nothing rather than throwing. This runs on the
    /// indexing path and its result only ever WIDENS what vela is willing to call somebody
    /// else's file, so failing to read one leaves the files it would have covered loud -
    /// a false gap somebody can see - rather than taking the command down or silently
    /// dropping first-party code.
    /// </summary>
    private static XDocument? Parse(string file)
    {
        try
        {
            // DtdProcessing.Prohibit is the default for XmlReaderSettings and is set here
            // anyway, because this parses a file from a repository vela was pointed at: an
            // external DTD is a file read, and a network fetch, that Constraint 1 does not
            // allow and that nothing in a nuget.config needs.
            using var reader = XmlReader.Create(file, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });

            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every nuget.config from the filesystem root down to the starting directory, so the
    /// nearest one is applied last and therefore wins.
    ///
    /// The name is matched case-insensitively against the directory listing rather than
    /// probed with File.Exists, because NuGet's own file is NuGet.Config, its
    /// documentation writes nuget.config, and repositories carry both. On Windows and
    /// macOS the difference does not arise; on Linux it decides whether the file is read
    /// at all. Where a directory somehow holds more than one spelling, they are applied in
    /// ordinal name order, so the answer does not depend on what the filesystem hands back
    /// first (Constraint 1).
    /// </summary>
    private static IReadOnlyList<string> DirectoryChainConfigFiles(string startDirectory)
    {
        var chain = new List<string>();

        for (var current = SafeDirectory(startDirectory); current is not null; current = current.Parent)
            chain.Add(current.FullName);

        chain.Reverse();

        var files = new List<string>();
        foreach (var directory in chain) files.AddRange(ConfigFilesIn(directory, "nuget.config"));
        return files;
    }

    private static DirectoryInfo? SafeDirectory(string path)
    {
        try
        {
            return new DirectoryInfo(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                      or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The files in one directory whose name matches, case-insensitively, ordered
    /// ordinally by full path.
    /// </summary>
    private static IReadOnlyList<string> ConfigFilesIn(string directory, string name)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Where(file => string.Equals(
                        Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.Ordinal)
                    .ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The machine-wide and user-level files for this platform, root-most first, so the
    /// directory chain that follows them wins over both.
    ///
    /// The locations are the ones NuGet documents. Two user-level locations are read on
    /// Unix rather than one: NuGet has used ~/.nuget/NuGet/NuGet.Config historically and
    /// $XDG_CONFIG_HOME (or ~/.config) since, and a machine can carry either. Reading both
    /// can only widen what vela calls a package folder, and every entry in either file was
    /// written by somebody configuring NuGet.
    /// </summary>
    private static IReadOnlyList<string> AmbientConfigFiles()
    {
        var files = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // Machine-wide: every *.config in the directory, in name order, which is the
            // order NuGet applies them in.
            foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                var programFiles = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(programFiles)) continue;

                var machine = Path.Combine(programFiles, "NuGet", "Config");
                if (!Directory.Exists(machine)) continue;

                files.AddRange(AllConfigFilesIn(machine));
                break;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                files.AddRange(ConfigFilesIn(Path.Combine(appData, "NuGet"), "NuGet.Config"));

            return files;
        }

        var machineDirectory = OperatingSystem.IsMacOS()
            ? "/Library/Application Support/NuGet/Config"
            : "/etc/opt/NuGet/Config";
        files.AddRange(AllConfigFilesIn(machineDirectory));

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var userDirectories = new List<string>();
        if (!string.IsNullOrEmpty(xdg)) userDirectories.Add(Path.Combine(xdg, "NuGet"));
        else if (!string.IsNullOrEmpty(profile)) userDirectories.Add(Path.Combine(profile, ".config", "NuGet"));
        if (!string.IsNullOrEmpty(profile)) userDirectories.Add(Path.Combine(profile, ".nuget", "NuGet"));

        foreach (var directory in userDirectories)
            files.AddRange(ConfigFilesIn(directory, "NuGet.Config"));

        return files;
    }

    /// <summary>
    /// Every *.config in a machine-wide directory, in ordinal name order. NuGet applies
    /// all of them rather than one, so a machine that splits its policy across several
    /// files is read the way it was written.
    /// </summary>
    private static IReadOnlyList<string> AllConfigFilesIn(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Where(file => string.Equals(
                        Path.GetExtension(file), ".config", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.Ordinal)
                    .ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
