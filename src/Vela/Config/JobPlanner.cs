namespace Vela.Config;

/// <summary>
/// A job vela cannot run itself, and has not yet been handed the output of.
/// </summary>
/// <param name="Source">
/// The absolute path of the `.scip` the job expects. It is the key deliberately: `vela
/// import` already records its verdict under <c>Path.GetFullPath(file)</c> and already
/// clears that key when the same file imports cleanly, so writing a pending job under the
/// path it is waiting for makes importing that file settle the job with no new machinery
/// and nothing left to go stale.
/// </param>
public sealed record PendingJob(string Source, string Detail);

/// <summary>
/// What one `vela index` run can and cannot do about a configuration.
/// </summary>
/// <param name="RunsVelaIndexer">
/// Whether any job asks for vela's own indexer. False means there is nothing for `vela
/// index` to build and the verb says so rather than building a C# index nobody asked for.
/// </param>
/// <param name="Problems">
/// Configuration that can never be satisfied by any import - a job root that is not there.
/// Recomputed on every index, so it clears when the config or the tree is fixed.
/// </param>
/// <param name="Notes">
/// True things a reader would otherwise be surprised by, which are not faults.
/// </param>
public sealed record IndexPlan(
    bool RunsVelaIndexer,
    IReadOnlyList<string> VelaLanguages,
    IReadOnlyList<PendingJob> Pending,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes)
{
    public bool Degraded => Pending.Count > 0 || Problems.Count > 0;
}

/// <summary>
/// Turns a configuration into what this run of `vela index` will do about it.
///
/// <b>The decision this class encodes: vela does not run other indexers.</b> `scip-io`
/// exists for orchestration, and vela's part is to consume merged output, so a job whose
/// indexer is not `vela` describes where its `.scip` is expected to come from rather than
/// naming a program for vela to execute. Nothing here starts a process. The most that is
/// asked of the outside world is whether a name resolves on PATH, so the message can say
/// "install it" rather than "run it".
///
/// <b>What such a job therefore does.</b> `vela index` deletes the database and rebuilds
/// it, so after any index run every foreign job is by definition not imported yet. Each one
/// becomes a <see cref="PendingJob"/>: the index is recorded as missing that language, every
/// answer carries the banner, the exit code is 3, and the message names the command that
/// would fix it. Importing the `.scip` the job named clears it. That is the whole of
/// Constraint 3 for this feature - a config file must never become a quiet way to lose a
/// language - and it is why silently ignoring a foreign job was never an option.
///
/// The three failures the brief asks about all land somewhere honest. An indexer that is
/// not installed is said inside the pending message, because a job can legitimately be run
/// through `npx` with nothing on PATH and a hard error there would be wrong. A root that
/// does not exist is a <see cref="IndexPlan.Problems"/> entry, because no import can ever
/// settle it. An indexer that runs and fails leaves no `.scip` to import, or leaves one that
/// `vela import` refuses, and in both cases the pending row is still there, because only a
/// clean import of that exact file removes it.
/// </summary>
public static class JobPlanner
{
    public static IndexPlan For(
        VelaConfig config, string repositoryRoot, Func<string, bool>? indexerIsInstalled = null)
    {
        var installed = indexerIsInstalled ?? Executable.IsOnPath;

        var velaLanguages = new List<string>();
        var pending = new List<PendingJob>();
        var problems = new List<string>();
        var notes = new List<string>();

        foreach (var job in config.Jobs)
        {
            var root = Path.GetFullPath(Path.Combine(repositoryRoot, job.Root));

            if (!Directory.Exists(root))
            {
                problems.Add($"job-root-missing: the {job.Language} job is rooted at '{job.Root}', which is "
                           + "not a directory in this repository, so that job can never run and no "
                           + $"{job.Language} from it is in this index.");
                continue;
            }

            if (job.IsVela)
            {
                velaLanguages.Add(job.Language);
                continue;
            }

            var missing = !installed(job.Indexer)
                ? $" {job.Indexer} was not found on PATH, so it is either not installed or is run some "
                  + "other way, such as through npx."
                : "";

            pending.Add(new PendingJob(
                job.ScipPath(repositoryRoot),
                $"nothing has been imported from it, and {VelaConfig.FileName} declares a {job.Language} job "
                + $"rooted at '{job.Root}' that produces it, so no {job.Language} from there is in this "
                + $"index.{missing} Run {job.Indexer} in '{job.Root}', then: vela import "
                + job.RelativeScipPath()));
        }

        // Said because it is surprising and true, not because it is wrong. Razor views and
        // Blazor components reach the compiler through the Razor source generator, into the
        // same compilation as the C#, so vela cannot produce one without the other. A
        // config naming one gets both, and an index holding a language nobody asked for is
        // a far smaller sin than one silently missing a language somebody did.
        var csharp = velaLanguages.Contains("csharp");
        var razor = velaLanguages.Contains("razor");
        if (csharp != razor)
        {
            notes.Add("vela's own indexer compiles csharp and razor as one compilation, because Razor views "
                    + "and Blazor components reach it through a source generator. Both are in this index "
                    + $"whether or not a job names them, so the {(csharp ? "razor" : "csharp")} documents are "
                    + "there too.");
        }

        return new IndexPlan(velaLanguages.Count > 0, velaLanguages, pending, problems, notes);
    }
}

/// <summary>
/// Whether a program name resolves to something runnable on PATH.
///
/// It looks; it never runs anything. That is the line this whole feature is on the right
/// side of: `vela index` reports what somebody else's indexer has not yet produced, and
/// executing that indexer would make vela an orchestrator of tools it cannot vouch for.
/// </summary>
public static class Executable
{
    public static bool IsOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // A name with a directory component is a path rather than something to look up,
        // which is how a repository points at an indexer it vendored.
        if (name.Contains('/') || name.Contains('\\'))
            return File.Exists(Path.GetFullPath(name));

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        // PATHEXT is Windows only, and an empty extension is what makes the same loop
        // answer for a bare name on Linux and macOS.
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Prepend("")
                .ToArray()
            : new[] { "" };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, name + extension))) return true;
                }
                catch (ArgumentException)
                {
                    // A PATH entry with invalid characters in it is not a directory any
                    // indexer is installed in, and it is not a reason to fail the index.
                }
            }
        }

        return false;
    }
}
