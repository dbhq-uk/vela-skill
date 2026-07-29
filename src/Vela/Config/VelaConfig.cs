using System.Text.Json;
using Vela.Indexing;

namespace Vela.Config;

/// <summary>
/// One indexing job: a language, the indexer that produces it, and the directory it is
/// rooted at.
///
/// A jobs ARRAY rather than a flat language list, following Sourcegraph's `index_jobs`,
/// because "which language" and "which indexer produced it" are separate facts and one
/// repository can need two jobs for one language at different roots. The real
/// configuration this was designed against needs exactly that: TypeScript under the mobile
/// app and JavaScript under the web app, both from scip-typescript.
/// </summary>
/// <param name="Include">
/// Which files under <paramref name="Root"/> the job covers, gitignore-style, empty
/// meaning all of them. It scopes what the job CLAIMS, not what vela indexes: vela does not
/// run other indexers, so this is documentation of the job's remit and the thing that keeps
/// the census honest about a job that only covers part of a language.
/// </param>
/// <param name="Index">
/// Where this job's `.scip` is expected to be, relative to <paramref name="Root"/>. It
/// defaults to `index.scip`, which is what scip-typescript, scip-python and scip-go all
/// write by default. For a job vela cannot run itself, this path is the whole contract:
/// `vela index` names it as outstanding, and importing exactly that file is what settles
/// the job.
/// </param>
public sealed record IndexJob(
    string Language,
    string Indexer,
    string Root,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string Index)
{
    /// <summary>The one indexer vela runs itself: its own Roslyn harvest.</summary>
    public const string VelaIndexer = "vela";

    /// <summary>
    /// The languages vela's own indexer can produce. Anything else asked of `vela` is a
    /// config error rather than a best effort, because Constraint 1 says language
    /// selection is explicit or defaulted and never guessed: there is no reading of
    /// `{"language": "python", "indexer": "vela"}` that vela can honour.
    /// </summary>
    public static readonly string[] VelaLanguages = { "csharp", "vb", "razor" };

    public bool IsVela => string.Equals(Indexer, VelaIndexer, StringComparison.Ordinal);

    /// <summary>The absolute path of the `.scip` this job is expected to produce.</summary>
    public string ScipPath(string repositoryRoot) =>
        Path.GetFullPath(Path.Combine(repositoryRoot, Root, Index));

    /// <summary>That same path relative to the repository, which is what a command line
    /// printed for a human to run should say.</summary>
    public string RelativeScipPath() =>
        (Root == "." ? Index : Root + "/" + Index).Replace('\\', '/');
}

/// <summary>
/// Everything wrong with a `vela.json`, in one exception, so a user fixes all of it in one
/// pass rather than one problem per run.
/// </summary>
public sealed class VelaConfigException : Exception
{
    public IReadOnlyList<string> Problems { get; }

    public VelaConfigException(IReadOnlyList<string> problems)
        : base(string.Join(" ", problems)) => Problems = problems;
}

/// <summary>
/// `vela.json`: which languages a repository wants indexed, and what is not source code.
///
/// <b>Why the file exists at all.</b> Measured on the repository vela is developed against,
/// a naive count by extension reports 5,775 Python files, of which 5,695 are vendored
/// `venv` and `site-packages`. The honest first-party picture is 1,866 C#, 307 Razor, 151
/// JavaScript, 80 Python, 30 TypeScript, 40 SQL and 3 Java. Worse, one directory of that
/// repository is gitignored build output of the mobile app - minified bundles, hash-named
/// chunks, a service worker - holding 64 of its 81 JavaScript files, so indexing it would
/// index the same code twice, once as readable source and once as generated bundles, and
/// every hit in the second copy would be unopenable. A directory that appears and
/// disappears between builds would also make the index non-deterministic, which is
/// Constraint 1 broken for reasons that would be miserable to debug. So the opinionated
/// default excludes are the feature and this file is how a repository overrides them.
///
/// <b>JSON, at the solution root, because that is what .NET established.</b> `global.json`
/// configures the SDK and `dotnet-tools.json` the local tool manifest; a .NET developer
/// expects JSON in a named file. EditorConfig is the INI-style exception and is scoped to
/// editor behaviour.
///
/// <b>No file means exactly what vela did before there was one.</b> <see cref="Default"/>
/// is the two jobs vela's own indexer has always produced, and nothing about the harvest
/// consults the exclude list: what Roslyn compiles is what gets indexed, so a file under
/// `obj` that the compiler was handed is still in the index whatever this file says. The
/// excludes govern what vela SAYS about the repository, not what the compiler saw, and
/// that is the only reading under which an existing user sees no change whatever.
/// </summary>
public sealed record VelaConfig(
    int Version,
    string? Solution,
    IReadOnlyList<IndexJob> Jobs,
    IReadOnlyList<string> Exclude,
    string? FoundAt)
{
    public const string FileName = "vela.json";

    /// <summary>The only version this vela reads.</summary>
    public const int SupportedVersion = 1;

    /// <summary>
    /// What is not first-party source, unless a repository says otherwise.
    ///
    /// Every entry earns its place by being somebody else's code or a build's output, and
    /// the directory ones are written as directories rather than as `dir/**` so the walk
    /// can prune them unread - see <see cref="PathFilter"/>. A repository takes one back
    /// with a negation, `"exclude": ["!**/dist/"]`, which is the cumulative
    /// `--languages=+C,-Java` idea from ctags said in the syntax already chosen here.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludes = new[]
    {
        // Version control metadata and editor state.
        "**/.git/", "**/.hg/", "**/.svn/", "**/.vs/", "**/.idea/",

        // .NET build output.
        "**/bin/", "**/obj/",

        // Somebody else's JavaScript: installed packages, and the libman convention for
        // client-side libraries in an ASP.NET project.
        "**/node_modules/", "**/bower_components/", "**/wwwroot/lib/",

        // Somebody else's Python. These two are the 5,695 files.
        "**/venv/", "**/.venv/", "**/site-packages/", "**/__pycache__/", "**/.tox/",

        // Vendored dependencies by the Go, PHP and CocoaPods conventions.
        "**/vendor/", "**/Pods/",

        // Build output by convention: JavaScript bundlers, Maven and Cargo, coverage.
        "**/dist/", "**/out/", "**/.next/", "**/.nuxt/", "**/.svelte-kit/",
        "**/target/", "**/coverage/", "**/.gradle/",

        // Generated files rather than generated directories.
        "**/*.min.js", "**/*.min.css", "**/*.map"
    };

    /// <summary>
    /// The two jobs vela's own indexer has always run, and the reason it is two: C# and
    /// Razor are separate languages to a reader and one compilation to Roslyn.
    /// </summary>
    public static IReadOnlyList<IndexJob> DefaultJobs { get; } = new[]
    {
        Job("csharp"),
        Job("razor")
    };

    /// <summary>Exactly today's behaviour: what vela does for a repository that has never
    /// heard of this file.</summary>
    public static VelaConfig Default { get; } =
        new(SupportedVersion, null, DefaultJobs, DefaultExcludes, null);

    /// <summary>
    /// The config governing a directory: the nearest `vela.json` at or above it, bounded by
    /// the repository root, or the defaults when there is none.
    /// </summary>
    /// <exception cref="VelaConfigException">The file is there and cannot be honoured.</exception>
    public static VelaConfig ForDirectory(string startDirectory)
    {
        var path = Find(startDirectory);
        return path is null ? Default : Load(path);
    }

    /// <summary>
    /// Where the `vela.json` for a directory is, or null.
    ///
    /// The search starts at the directory and walks up to the repository root inclusive,
    /// because `repo/src/App.sln` is an ordinary layout and the file belongs to the
    /// repository rather than to the solution's own folder. It stops AT the repository
    /// root, and that bound is deliberate: without it one stray `vela.json` in a home
    /// directory would silently reconfigure every repository under it, and what the index
    /// contained would depend on where the clone happened to live (Constraint 1).
    /// </summary>
    public static string? Find(string startDirectory)
    {
        var start = Path.GetFullPath(startDirectory);
        var root = ProjectRoot.ForSolutionDirectory(start);

        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, FileName);
            if (File.Exists(candidate)) return candidate;

            if (string.Equals(current.FullName, root, StringComparison.Ordinal)) break;
        }

        return null;
    }

    /// <summary>
    /// Reads a `vela.json`, or throws with everything wrong with it.
    ///
    /// It is strict, and the reason is Constraint 3. A misspelled key is the config failure
    /// with the worst consequence: `excludes` for `exclude` quietly indexes the vendored
    /// tree, and `jobbs` for `jobs` quietly loses a language. Both are an incomplete index
    /// looking like a complete one, which is the failure this project exists to prevent, so
    /// an unknown property is refused by name rather than ignored.
    /// </summary>
    public static VelaConfig Load(string path)
    {
        var problems = new List<string>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new VelaConfigException(new[]
            {
                $"{FileName} at {path} could not be read: {ex.Message}"
            });
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new VelaConfigException(new[]
                {
                    $"{FileName} at {path} is a {Describe(root.ValueKind)} where vela expects an object "
                    + "with the properties version, solution, jobs and exclude."
                });
            }

            var version = SupportedVersion;
            string? solution = null;
            IReadOnlyList<IndexJob>? jobs = null;
            var exclude = new List<string>(DefaultExcludes);

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "$schema":
                        break;

                    case "version":
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out version))
                        {
                            problems.Add($"{FileName} at {path} has a version that is not a whole number.");
                        }
                        else if (version != SupportedVersion)
                        {
                            problems.Add($"{FileName} at {path} declares version {version}, and this vela "
                                       + $"reads version {SupportedVersion}. A later version may mean things "
                                       + "this vela would honour only half of.");
                        }

                        break;

                    case "solution":
                        var named = AsString(property.Value);
                        if (string.IsNullOrWhiteSpace(named))
                            problems.Add($"{FileName} at {path} has a solution that is not a path.");
                        else
                            solution = Path.GetFullPath(Path.Combine(directory, named));

                        break;

                    case "jobs":
                        if (property.Value.ValueKind != JsonValueKind.Array)
                        {
                            problems.Add($"{FileName} at {path} has a jobs property that is not an array.");
                            break;
                        }

                        jobs = ReadJobs(property.Value, path, problems);
                        break;

                    case "exclude":
                        if (property.Value.ValueKind != JsonValueKind.Array)
                        {
                            problems.Add($"{FileName} at {path} has an exclude property that is not an array.");
                            break;
                        }

                        // Appended to the defaults rather than replacing them, so a
                        // repository states what is different about it and nothing else,
                        // and so a later `!` pattern can take a default back.
                        exclude.AddRange(ReadStrings(property.Value, path, "exclude", problems));
                        break;

                    default:
                        problems.Add($"{FileName} at {path} has a property vela does not know: "
                                   + $"'{property.Name}'. The ones it reads are $schema, version, solution, "
                                   + "jobs and exclude.");
                        break;
                }
            }

            if (problems.Count > 0) throw new VelaConfigException(problems);

            return new VelaConfig(version, solution, jobs ?? DefaultJobs, exclude, Path.GetFullPath(path));
        }
    }

    private static IReadOnlyList<IndexJob> ReadJobs(JsonElement array, string path, List<string> problems)
    {
        var jobs = new List<IndexJob>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;

        foreach (var element in array.EnumerateArray())
        {
            position++;

            if (element.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{FileName} at {path} has a job at position {position} that is not an object.");
                continue;
            }

            string? language = null;
            var indexer = IndexJob.VelaIndexer;
            var root = ".";
            var index = "index.scip";
            var include = new List<string>();
            var exclude = new List<string>();

            foreach (var property in element.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "language":
                        language = AsString(property.Value)?.ToLowerInvariant();
                        break;
                    case "indexer":
                        indexer = AsString(property.Value) ?? "";
                        break;
                    case "root":
                        root = NormaliseRoot(AsString(property.Value));
                        break;
                    case "index":
                        index = (AsString(property.Value) ?? "").Replace('\\', '/').Trim('/');
                        break;
                    case "include":
                        include.AddRange(ReadStrings(property.Value, path, "include", problems));
                        break;
                    case "exclude":
                        exclude.AddRange(ReadStrings(property.Value, path, "exclude", problems));
                        break;
                    default:
                        problems.Add($"{FileName} at {path} has a job property vela does not know: "
                                   + $"'{property.Name}'. The ones it reads are language, indexer, root, "
                                   + "index, include and exclude.");
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(language))
            {
                problems.Add($"{FileName} at {path} has a job at position {position} with no language. "
                           + "Language selection is explicit or defaulted and never guessed from a file's "
                           + "contents, so a job without one cannot be read at all.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(indexer))
            {
                problems.Add($"{FileName} at {path} has a {language} job whose indexer is empty. Name the "
                           + $"indexer that produces it, or '{IndexJob.VelaIndexer}' for vela's own.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(index))
            {
                problems.Add($"{FileName} at {path} has a {language} job whose index is empty. That is the "
                           + "path of the .scip the job produces, and vela has to be able to name it.");
                continue;
            }

            var isVela = string.Equals(indexer, IndexJob.VelaIndexer, StringComparison.Ordinal);

            if (isVela && !IndexJob.VelaLanguages.Contains(language, StringComparer.Ordinal))
            {
                problems.Add($"{FileName} at {path} asks vela's own indexer for {language}, which it cannot "
                           + $"produce. It produces {string.Join(", ", IndexJob.VelaLanguages)}. Name the "
                           + $"indexer that does produce {language} and import its .scip.");
                continue;
            }

            if (isVela && root != ".")
            {
                problems.Add($"{FileName} at {path} roots the {language} job at '{root}'. vela's own indexer "
                           + "indexes whatever the solution compiles, rooted at the repository, so its jobs "
                           + "are rooted at '.' and nothing else.");
                continue;
            }

            if (isVela && (include.Count > 0 || exclude.Count > 0))
            {
                problems.Add($"{FileName} at {path} gives the {language} job an include or exclude list. "
                           + "vela indexes what the compiler was handed, so a file list cannot narrow it: "
                           + "what is in the compilation is what is in the index.");
                continue;
            }

            if (!seen.Add($"{language} {indexer} {root}"))
            {
                problems.Add($"{FileName} at {path} names the same job twice: {language} from {indexer} at "
                           + $"'{root}'. Two identical jobs cannot be told apart, so one of them could never "
                           + "be reported on.");
                continue;
            }

            jobs.Add(new IndexJob(language, indexer, root, include, exclude, index));
        }

        return jobs;
    }

    private static IEnumerable<string> ReadStrings(
        JsonElement element, string path, string name, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{FileName} at {path} has a {name} property that is not an array of patterns.");
            yield break;
        }

        foreach (var item in element.EnumerateArray())
        {
            var text = AsString(item);
            if (string.IsNullOrWhiteSpace(text))
            {
                problems.Add($"{FileName} at {path} has an empty entry in a {name} list.");
                continue;
            }

            yield return text;
        }
    }

    private static string? AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static string NormaliseRoot(string? root)
    {
        var text = (root ?? "").Replace('\\', '/').Trim();
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        text = text.Trim('/');
        return text.Length == 0 ? "." : text;
    }

    private static string Describe(JsonValueKind kind) => kind.ToString().ToLowerInvariant();

    private static IndexJob Job(string language) =>
        new(language, IndexJob.VelaIndexer, ".", Array.Empty<string>(), Array.Empty<string>(), "index.scip");
}
