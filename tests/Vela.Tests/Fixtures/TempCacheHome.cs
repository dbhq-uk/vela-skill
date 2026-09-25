namespace Vela.Tests.Fixtures;

/// <summary>
/// Points vela's index cache at a throwaway directory for the life of one test, and puts
/// the environment back afterwards. Every test that indexes, imports or queries through
/// the CLI uses this one class.
///
/// <b>It sets VELA_CACHE_HOME, because that is the variable that wins.</b> The index
/// directory is resolved from VELA_CACHE_HOME first, then XDG_CACHE_HOME, then the user
/// profile (<see cref="Vela.Indexing.IndexPaths"/>). There used to be one copy of this
/// class per test class, and every copy set XDG_CACHE_HOME only. So in a shell with
/// VELA_CACHE_HOME set, every test wrote to that one real directory instead of its own,
/// the tests saw each other's indexes, and `vela cache clear --all` in IndexCacheTests
/// removed every index the developer had. CI never sets the variable, which is why CI
/// stayed green. One class means one place to get this right.
///
/// XDG_CACHE_HOME is left as it is. It cannot matter while VELA_CACHE_HOME is set, and
/// a test that wants to exercise it uses <see cref="CacheEnvironment"/> directly.
///
/// Environment variables are process-wide, so a test using this must sit in the
/// <c>EnvironmentSensitive</c> collection, which never runs beside anything else.
/// </summary>
public sealed class TempCacheHome : IDisposable
{
    private readonly CacheEnvironment _environment;

    /// <summary>The throwaway directory the index cache now resolves under.</summary>
    public string Path { get; }

    public TempCacheHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vela-cache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
        _environment = CacheEnvironment.Save().With(CacheEnvironment.VelaCacheHome, Path);
    }

    public void Dispose()
    {
        _environment.Dispose();
        try { Directory.Delete(Path, recursive: true); } catch { /* temp dir, best effort */ }
    }
}

/// <summary>
/// Saves both cache variables, sets the ones a test names, and restores both on dispose.
/// Setting a variable to null removes it.
///
/// For the few tests that are about how the cache directory is resolved, rather than
/// about using one. A test of XDG_CACHE_HOME has to clear VELA_CACHE_HOME as well, or a
/// value in the developer's shell answers instead of the one the test set.
/// </summary>
public sealed class CacheEnvironment : IDisposable
{
    public const string VelaCacheHome = "VELA_CACHE_HOME";
    public const string XdgCacheHome = "XDG_CACHE_HOME";

    private static readonly string[] Variables = [VelaCacheHome, XdgCacheHome];

    private readonly Dictionary<string, string?> _previous = new();

    private CacheEnvironment()
    {
        foreach (var name in Variables)
            _previous[name] = Environment.GetEnvironmentVariable(name);
    }

    /// <summary>Saves both variables and changes neither. Chain <see cref="With"/> to set them.</summary>
    public static CacheEnvironment Save() => new();

    /// <summary>Sets one variable for the life of this scope.</summary>
    public CacheEnvironment With(string name, string? value)
    {
        if (!_previous.ContainsKey(name))
            throw new ArgumentException($"{name} is not a cache variable this scope restores.", nameof(name));
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}
