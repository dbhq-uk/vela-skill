using Vela.Indexing;
using Xunit;

/// <summary>
/// The package cache is one of the two locations that can show a file belongs to somebody
/// else rather than being missing from the repository, and vela read it from
/// NUGET_PACKAGES or from ~/.nuget/packages and nowhere else. A repository that puts its
/// packages somewhere through nuget.config - the documented, supported way to do it - had
/// every package file classified as a gap in its own code, so the index was permanently
/// degraded and every answer carried a false INCOMPLETE banner. That is the crying-wolf
/// failure Constraint 3 cuts both ways on.
///
/// These tests pin the parse. The machine-wide and user-level halves of the chain are
/// passed in rather than read, so whatever the machine running the suite happens to have
/// in ~/.config/NuGet cannot change the answer (Constraint 1). Every one of them also
/// pins NUGET_PACKAGES, which is process-wide, so they share the non-parallel collection
/// with every other test that mutates the environment.
/// </summary>
[Collection(EnvironmentSensitive.Name)]
public class NuGetPackageFoldersTests
{
    [Fact]
    public void Resolve_ReadsGlobalPackagesFolderFromANugetConfigBesideTheSolution()
    {
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var packages = tree.Directory("elsewhere", "packages");

        tree.WriteConfig(tree.Root, $"""
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packages}" />
              </config>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(tree.Root, NoAmbientConfiguration);

        Assert.Equal(packages, folders.GlobalPackagesFolder);
    }

    [Fact]
    public void Resolve_ResolvesARelativeFolderAgainstTheConfigThatDeclaredIt()
    {
        // NuGet resolves a relative value against the directory of the nuget.config that
        // holds it, not against the current directory and not against the project.
        // Reading it any other way names a directory that does not exist, and a root that
        // is not there classifies nothing, so every package file goes back to being a
        // loud gap.
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var repository = tree.Directory("repo");

        tree.WriteConfig(repository, """
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="../shared/packages" />
              </config>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(repository, NoAmbientConfiguration);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(tree.Root, "shared", "packages")),
            folders.GlobalPackagesFolder);
    }

    [Fact]
    public void Resolve_PrefersTheNearestConfigWhenTwoOnTheWayUpDeclareOne()
    {
        // The documented order walks from the project directory upwards and lets the
        // nearest file win, because the nearest file is the one the repository wrote
        // about itself.
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var near = tree.Directory("far", "near");
        var outer = tree.Directory("outer-packages");
        var inner = tree.Directory("inner-packages");

        tree.WriteConfig(tree.Root, $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{outer}" /></config>
            </configuration>
            """);
        tree.WriteConfig(near, $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{inner}" /></config>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(near, NoAmbientConfiguration);

        Assert.Equal(inner, folders.GlobalPackagesFolder);
    }

    [Fact]
    public void Resolve_ReadsTheFileWhateverCaseItsNameIsWrittenIn()
    {
        // NuGet's own file is NuGet.Config, the documentation writes nuget.config, and
        // repositories in the wild carry both. On Windows and macOS the difference does
        // not arise; on Linux it decides whether the file is read at all, which is the
        // difference between a correct index and a permanently degraded one.
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var packages = tree.Directory("packages");

        File.WriteAllText(Path.Combine(tree.Root, "NuGet.Config"), $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{packages}" /></config>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(tree.Root, NoAmbientConfiguration);

        Assert.Equal(packages, folders.GlobalPackagesFolder);
    }

    [Fact]
    public void Resolve_ReadsFallbackPackageFoldersAndHonoursClear()
    {
        // fallbackPackageFolders is additive down the chain and <clear /> is the one
        // thing that empties it, so a repository that clears the machine's folders and
        // names its own has exactly the folders it named.
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var inner = tree.Directory("inner");
        var outerFallback = tree.Directory("outer-fallback");
        var innerFallback = tree.Directory("inner-fallback");

        tree.WriteConfig(tree.Root, $"""
            <configuration>
              <fallbackPackageFolders>
                <add key="Outer" value="{outerFallback}" />
              </fallbackPackageFolders>
            </configuration>
            """);
        tree.WriteConfig(inner, $"""
            <configuration>
              <fallbackPackageFolders>
                <clear />
                <add key="Inner" value="{innerFallback}" />
              </fallbackPackageFolders>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(inner, NoAmbientConfiguration);

        Assert.Equal(new[] { innerFallback }, folders.FallbackPackageFolders);
    }

    [Fact]
    public void Resolve_AccumulatesFallbackPackageFoldersDownTheChainWhenNothingClearsThem()
    {
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var inner = tree.Directory("inner");
        var outerFallback = tree.Directory("outer-fallback");
        var innerFallback = tree.Directory("inner-fallback");

        tree.WriteConfig(tree.Root, $"""
            <configuration>
              <fallbackPackageFolders><add key="Outer" value="{outerFallback}" /></fallbackPackageFolders>
            </configuration>
            """);
        tree.WriteConfig(inner, $"""
            <configuration>
              <fallbackPackageFolders><add key="Inner" value="{innerFallback}" /></fallbackPackageFolders>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(inner, NoAmbientConfiguration);

        Assert.Equal(new[] { outerFallback, innerFallback }, folders.FallbackPackageFolders);
    }

    [Fact]
    public void Resolve_LetsTheEnvironmentVariableWinOverTheConfig()
    {
        // NUGET_PACKAGES beats every configuration file, which is the order NuGet
        // documents and the order a CI runner depends on.
        using var tree = new TempTree();
        var fromEnvironment = tree.Directory("from-environment");
        using var _ = new PackageCacheVariable(fromEnvironment);

        tree.WriteConfig(tree.Root, $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{tree.Directory("from-config")}" /></config>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(tree.Root, NoAmbientConfiguration);

        Assert.Equal(fromEnvironment, folders.GlobalPackagesFolder);
    }

    [Fact]
    public void Resolve_FallsBackToTheDocumentedDefaultWhenNothingDeclaresOne()
    {
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);

        var folders = NuGetPackageFolders.Resolve(tree.Root, NoAmbientConfiguration);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".nuget", "packages"), folders.GlobalPackagesFolder);
    }

    [Fact]
    public void Resolve_ReadsAmbientMachineAndUserConfigurationBelowTheDirectoryChain()
    {
        // The machine-wide and user-level files sit UNDER the directory chain in the
        // documented order, so anything the repository says wins over them and anything
        // it does not say falls through to them.
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var userPackages = tree.Directory("user-packages");
        var machineFallback = tree.Directory("machine-fallback");
        var repositoryFallback = tree.Directory("repo-fallback");

        var ambient = Path.Combine(tree.Root, "user.config");
        File.WriteAllText(ambient, $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{userPackages}" /></config>
              <fallbackPackageFolders><add key="Machine" value="{machineFallback}" /></fallbackPackageFolders>
            </configuration>
            """);

        var repository = tree.Directory("repo");
        tree.WriteConfig(repository, $"""
            <configuration>
              <fallbackPackageFolders><add key="Repo" value="{repositoryFallback}" /></fallbackPackageFolders>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(repository, new[] { ambient });

        Assert.Equal(userPackages, folders.GlobalPackagesFolder);
        Assert.Equal(new[] { machineFallback, repositoryFallback }, folders.FallbackPackageFolders);
    }

    [Fact]
    public void Resolve_KeepsTheDocumentedDefaultAmongTheFoldersItClassifiesBy()
    {
        // The one deliberate difference between "which folder will restore write to",
        // which is what NuGet's precedence answers, and "which folders could hold a
        // restored package", which is the only question vela asks. A configured folder
        // REPLACES the default for restore; it does not empty the default of the packages
        // already in it, and a file from there is still nobody's first-party code.
        // Dropping the default would swap one crying-wolf banner for another.
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var configured = tree.Directory("from-config");
        var fallback = tree.Directory("shared-fallback");

        tree.WriteConfig(tree.Root, $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{configured}" /></config>
              <fallbackPackageFolders><add key="Shared" value="{fallback}" /></fallbackPackageFolders>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(tree.Root, NoAmbientConfiguration);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Contains(configured, folders.EveryPackageFolder);
        Assert.Contains(fallback, folders.EveryPackageFolder);
        Assert.Contains(Path.Combine(profile, ".nuget", "packages"), folders.EveryPackageFolder);
    }

    [Fact]
    public void Resolve_IgnoresAConfigItCannotParseRatherThanThrowing()
    {
        // A malformed nuget.config must not take a query down, and it must not be read as
        // evidence either: with nothing resolved from it the files it would have covered
        // stay loud, which is the recoverable direction.
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);

        tree.WriteConfig(tree.Root, "<configuration><config><add key=");

        var folders = NuGetPackageFolders.Resolve(tree.Root, NoAmbientConfiguration);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".nuget", "packages"), folders.GlobalPackagesFolder);
    }

    [Fact]
    public void Resolve_TakesTheLastDeclarationWithinOneFileAndHonoursClearInTheConfigSection()
    {
        using var tree = new TempTree();
        using var _ = new PackageCacheVariable(null);
        var second = tree.Directory("second");

        tree.WriteConfig(tree.Root, $"""
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{tree.Directory("first")}" />
                <clear />
                <add key="globalPackagesFolder" value="{second}" />
              </config>
            </configuration>
            """);

        var folders = NuGetPackageFolders.Resolve(tree.Root, NoAmbientConfiguration);

        Assert.Equal(second, folders.GlobalPackagesFolder);
    }

    private static readonly IReadOnlyList<string> NoAmbientConfiguration = Array.Empty<string>();

    /// <summary>A directory tree that exists on disk, removed afterwards.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; }

        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "vela-nuget-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Directory(params string[] parts)
        {
            var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void WriteConfig(string directory, string xml) =>
            File.WriteAllText(Path.Combine(directory, "nuget.config"), xml);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Root, recursive: true); } catch { /* temp, best effort */ }
        }
    }

    /// <summary>Points NUGET_PACKAGES somewhere disposable, and puts it back.</summary>
    private sealed class PackageCacheVariable : IDisposable
    {
        private readonly string? _previous;

        public PackageCacheVariable(string? path)
        {
            _previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", path);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("NUGET_PACKAGES", _previous);
    }
}
