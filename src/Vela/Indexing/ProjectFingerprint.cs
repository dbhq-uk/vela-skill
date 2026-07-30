using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// One thing a project was built from, and what it held when the index was built.
/// </summary>
/// <param name="Kind">
/// What sort of input this is, from the constants on <see cref="ProjectFingerprint"/>.
/// It is part of the key because one path can legitimately arrive twice - an
/// .editorconfig is both an analyzer-config document and a file on disk - and because a
/// reader working out why a project rebuilt needs to know which channel named it.
/// </param>
/// <param name="Path">
/// Relative to the root the index is built at, with '/' separators, exactly as a
/// document path in this index is. A file outside that root - source contributed from
/// the NuGet package cache, a reference assembly from the .NET installation - keeps its
/// absolute path, because there is no honest relative one and '..' would be a lie about
/// where it lives.
/// </param>
/// <param name="ContentHash">
/// SHA-256 of the content, in lower-case hex, or <see cref="ProjectFingerprint.NotRead"/>
/// for an input whose content was deliberately not read. See
/// <see cref="ProjectFingerprint.ReferenceKind"/> for the only case of that.
/// </param>
public readonly record struct ProjectInput(string Kind, string Path, string ContentHash);

/// <summary>
/// What one project was built from, reduced to a single string that can be compared
/// against the same project's inputs on a later run.
///
/// <b>Why this exists.</b> A full rebuild cannot be stale, because it reads everything.
/// An incremental rebuild is a CLAIM that what it skipped has not changed, and nothing in
/// vela recorded which files fed which project, so there was nothing to check the claim
/// against. This is the record that makes the claim checkable.
///
/// <b>Why content hashes and not modification times.</b> <see cref="Staleness"/> compares
/// mtimes, and is right to: it runs on every query and only has to raise a suspicion, so
/// it must cost a directory walk and nothing more. A decision about what NOT to re-read is
/// a different question. An mtime changes when nothing did - a checkout, a `touch`, a
/// `git stash pop` - and does not change when something did, for a file restored with its
/// timestamp preserved. The first costs a needless rebuild; the second leaves the index
/// describing code that no longer exists while reporting itself complete, which is
/// Constraint 3's exact failure. So this costs a read.
///
/// <b>What a source-generated document's input is, and why.</b> Razor views and Blazor
/// components never reach the compiler as files: the Razor generator turns each one into
/// a .g.cs that exists only inside the compilation. Its text could be hashed, and it is
/// deliberately not. A generated document's content is DERIVED, so hashing it records a
/// consequence rather than a cause, and it cannot be compared against a tree without
/// running every generator again - which is most of the work incremental exists to avoid.
/// A fingerprint you can only compute by doing the expensive thing is not a fingerprint.
/// Roslyn already hands over the real input: a .cshtml or .razor arrives as an
/// <see cref="Project.AdditionalDocuments">additional document</see>, on disk and
/// readable, so it is hashed directly and nothing has to be inferred or mapped back. That
/// is what makes an edit to a view invalidate its project.
///
/// The gap that leaves is the generator ITSELF: a Razor SDK that emits different code
/// from the same view. That is why <see cref="ReferenceKind"/> is in the digest - an
/// analyzer or generator arrives as a path carrying its version, so an upgrade changes the
/// fingerprint even though every file in the repository is untouched.
///
/// <b>The bias throughout is to over-hash.</b> Every Directory.Build.props between the
/// project and the root is hashed, not merely the nearest one MSBuild would import; a file
/// that cannot be read is recorded as unreadable rather than passed over. Hashing
/// something that turns out not to matter costs a rebuild nobody needed. Missing something
/// that does matter costs a wrong answer that looks right.
/// </summary>
/// <param name="Project">
/// This project's identity: the project file relative to the root the index is built at,
/// so it is the same string in any checkout on any machine. A project whose Roslyn name is
/// not simply its file's name - which is what multi-targeting produces, one Roslyn project
/// per framework over one file - carries that name after a '#', because two compilations
/// described by one row would be a ledger that cannot be right about either.
/// </param>
public sealed record ProjectFingerprint(
    string Project,
    string Name,
    string Fingerprint,
    IReadOnlyList<ProjectInput> Inputs,
    IReadOnlyList<string> References)
{
    /// <summary>A file the compiler compiles: a .cs or .vb Roslyn was handed.</summary>
    public const string SourceKind = "source";

    /// <summary>
    /// A file handed to the generators rather than to the compiler, which is how a
    /// .cshtml and a .razor arrive. These are the inputs of the generated documents, and
    /// hashing them is what makes an edit to a view invalidate its project.
    /// </summary>
    public const string AdditionalKind = "additional";

    /// <summary>An .editorconfig or a generated .globalconfig.</summary>
    public const string AnalyzerConfigKind = "analyzerconfig";

    /// <summary>
    /// The project file, and every Directory.Build.props, Directory.Build.targets,
    /// Directory.Packages.props, global.json or NuGet.config between it and the root.
    /// None of them is a document and none of them is named by the project, and any of
    /// them can change what every file in the project compiles to.
    /// </summary>
    public const string BuildFileKind = "buildfile";

    /// <summary>
    /// An assembly the compilation reads: a NuGet or framework reference, or an analyzer
    /// and its generators. The PATH is hashed into the digest and the CONTENT is not, and
    /// that is the one deliberate hole in this record. Reading several hundred assemblies
    /// per project on every index would cost more than the rebuild it is trying to avoid,
    /// and the path already carries the version - a package cache path holds it as a
    /// directory - so an upgrade is caught. A rebuilt assembly at the same path and
    /// version is not.
    /// </summary>
    public const string ReferenceKind = "reference";

    /// <summary>
    /// The content hash of an input vela did not read: a reference, whose path is the
    /// evidence, or a file it could not open. Never a real hash, and never confusable
    /// with one, because a hash is 64 hex characters.
    /// </summary>
    public const string NotRead = "not-read";

    /// <summary>
    /// The content hash of a file that exists and would not open. A constant, so the
    /// fingerprint is stable while the file stays unreadable and changes the moment it
    /// becomes readable, which rebuilds the project. That is the safe direction: a
    /// rebuild nobody needed, rather than a skip nobody can justify.
    /// </summary>
    public const string Unreadable = "unreadable";

    /// <summary>
    /// The names walked for between the project and the root. Not the set MSBuild
    /// imports, which depends on properties vela does not evaluate, but a superset of it:
    /// every one found on the way up is hashed, including ones a nearer file would have
    /// stopped MSBuild from reaching. Hashing a file that turned out not to be imported
    /// costs a rebuild; missing one that was costs a wrong answer.
    /// </summary>
    private static readonly string[] BuildFileNames =
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "Directory.Build.rsp", "Directory.Solution.props", "Directory.Solution.targets",
        "global.json", "NuGet.config", "nuget.config", "NuGet.Config"
    };

    /// <summary>
    /// Which recipe produced the digest. It is the first line of the hashed text, so
    /// changing what goes into a fingerprint changes every fingerprint and every project
    /// rebuilds - which is what should happen, because the old ones describe a different
    /// question. The schema version does the same job for a reader that cannot open the
    /// database at all; this one covers a change that leaves the tables alone.
    /// </summary>
    private const string Recipe = "vela project fingerprint 1";

    /// <summary>
    /// Everything this project was built from, and the digest of it.
    /// </summary>
    /// <param name="projectRoot">
    /// The root the index is built at, from <see cref="ProjectRoot"/>. Every path in the
    /// result is relative to it, so a fingerprint does not change when a repository moves.
    /// </param>
    public static async Task<ProjectFingerprint> ForAsync(
        Project project, string projectRoot, CancellationToken ct)
    {
        var root = Path.GetFullPath(projectRoot);
        var inputs = new List<ProjectInput>();

        foreach (var document in project.Documents)
            inputs.Add(await InputOfAsync(SourceKind, document, root, ct));

        // The .cshtml and .razor files. They are not compiled and they are the reason
        // this tool exists, so they are the one kind of input whose absence would be
        // invisible until an answer came back describing a view as it used to be.
        foreach (var document in project.AdditionalDocuments)
            inputs.Add(await InputOfAsync(AdditionalKind, document, root, ct));

        foreach (var document in project.AnalyzerConfigDocuments)
            inputs.Add(await InputOfAsync(AnalyzerConfigKind, document, root, ct));

        inputs.AddRange(BuildFilesFor(project, root));

        foreach (var analyzer in project.AnalyzerReferences)
            inputs.Add(new ProjectInput(ReferenceKind, Relative(root, analyzer.FullPath ?? analyzer.Display ?? ""), NotRead));

        foreach (var metadata in project.MetadataReferences)
            inputs.Add(new ProjectInput(ReferenceKind, Relative(root, metadata.Display ?? ""), NotRead));

        // Sorted, and sorted here rather than by the caller, because the digest is taken
        // over this list: an order that came from the filesystem would give one unchanged
        // tree different fingerprints on two machines (Constraint 1).
        var ordered = inputs
            .Distinct()
            .OrderBy(i => i.Kind, StringComparer.Ordinal)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ThenBy(i => i.ContentHash, StringComparer.Ordinal)
            .ToList();

        var references = project.ProjectReferences
            .Select(reference => project.Solution.GetProject(reference.ProjectId))
            .Where(referenced => referenced is not null)
            .Select(referenced => IdentityOf(referenced!, root))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToList();

        var identity = IdentityOf(project, root);

        return new ProjectFingerprint(
            identity,
            project.Name,
            Digest(identity, project.Name, project.Language, ordered, references),
            ordered,
            references);
    }

    /// <summary>
    /// Every project of a solution, fingerprinted, without compiling any of them.
    ///
    /// This is what a rebuild plan is computed from, and it has to be cheap: compiling
    /// every project to find out which ones need compiling would cost exactly what the
    /// feature exists to avoid. Measured on the real solution, ten projects and 6,070
    /// inputs: 554ms cold and 76ms warm, against a full index of 2m13s.
    ///
    /// Projects Roslyn cannot compile at all are left out, because they are also left out
    /// of the ledger the harvest writes, and a list that held them would report the
    /// project set as having changed on every run. They are never skipped either: the
    /// harvest is told which projects to REUSE, so anything not named there is looked at.
    /// </summary>
    public static async Task<IReadOnlyList<ProjectFingerprint>> ForSolutionAsync(
        Project[] projects, string projectRoot, CancellationToken ct)
    {
        var fingerprints = new List<ProjectFingerprint>();

        foreach (var project in projects)
        {
            if (!project.SupportsCompilation) continue;
            fingerprints.Add(await ForAsync(project, projectRoot, ct));
        }

        fingerprints.Sort((left, right) => string.CompareOrdinal(left.Project, right.Project));
        return fingerprints;
    }

    /// <summary>
    /// What this project is called in the ledger: its file, relative to the root.
    ///
    /// A multi-targeted project is several Roslyn projects over one file, and they compile
    /// different code, so the Roslyn name is appended when it is not simply the file's own
    /// name. Two compilations sharing one row would be a record that is wrong about at
    /// least one of them, and the whole point of the row is to be right.
    /// </summary>
    public static string IdentityOf(Project project, string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        var file = project.FilePath;

        if (string.IsNullOrEmpty(file)) return "#" + project.Name;

        var path = Relative(root, file);
        return string.Equals(project.Name, Path.GetFileNameWithoutExtension(file), StringComparison.Ordinal)
            ? path
            : path + "#" + project.Name;
    }

    /// <summary>
    /// The digest. A single line per fact, in a fixed order, hashed: a reader who wants
    /// to know why two fingerprints differ can reconstruct the text from the stored
    /// inputs and diff it.
    /// </summary>
    private static string Digest(
        string identity, string name, string language,
        IReadOnlyList<ProjectInput> inputs, IReadOnlyList<string> references)
    {
        var text = new StringBuilder();
        text.Append(Recipe).Append('\n');
        text.Append("project ").Append(identity).Append('\n');
        text.Append("name ").Append(name).Append('\n');
        text.Append("language ").Append(language).Append('\n');

        foreach (var reference in references)
            text.Append("references ").Append(reference).Append('\n');

        foreach (var input in inputs)
            text.Append(input.Kind).Append(' ').Append(input.ContentHash).Append(' ').Append(input.Path).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// One document, hashed by the text Roslyn holds rather than by the bytes on disk.
    ///
    /// The text is what the compiler read, which is the thing being fingerprinted, and it
    /// is the only content an in-memory document has at all. It also settles a file whose
    /// bytes differ from another's only in encoding or in a byte-order mark: the compiler
    /// saw the same characters, so nothing has changed, and hashing the bytes would say
    /// otherwise.
    /// </summary>
    private static async Task<ProjectInput> InputOfAsync(
        string kind, TextDocument document, string root, CancellationToken ct)
    {
        var path = string.IsNullOrEmpty(document.FilePath)
            ? document.Name
            : Relative(root, document.FilePath);

        try
        {
            var text = await document.GetTextAsync(ct);
            return new ProjectInput(kind, path, HashOfText(text.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProjectInput(kind, path, Unreadable);
        }
    }

    /// <summary>
    /// The project file, and every build file above it up to the root the index is
    /// built at.
    ///
    /// The walk stops at that root. Above it is somebody else's tree: MSBuild would go on
    /// looking, but the index is not rooted there, the freshness check does not watch
    /// there, and hashing an unbounded number of parent directories on every index is not
    /// a cost worth paying for a layout nobody has. A project that sits outside the root
    /// altogether contributes only its own directory, for the same reason.
    /// </summary>
    private static IEnumerable<ProjectInput> BuildFilesFor(Project project, string root)
    {
        var found = new List<ProjectInput>();

        var file = project.FilePath;
        if (string.IsNullOrEmpty(file)) return found;

        found.Add(new ProjectInput(BuildFileKind, Relative(root, file), HashOfFile(file)));

        var start = Path.GetDirectoryName(Path.GetFullPath(file));
        if (start is null) return found;

        var levels = new List<string>();
        var reachedRoot = false;
        for (var directory = start; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            levels.Add(directory);
            if (SamePath(directory, root))
            {
                reachedRoot = true;
                break;
            }
        }

        if (!reachedRoot) levels = new List<string> { start };

        foreach (var level in levels) found.AddRange(BuildFilesIn(level, root));

        return found;
    }

    /// <summary>
    /// The build files directly in one directory. Listed rather than probed name by name,
    /// so a file is found under whatever casing the filesystem holds it in and is found
    /// exactly once.
    /// </summary>
    private static IEnumerable<ProjectInput> BuildFilesIn(string directory, string root)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be listed may hold a build file that changed, so a
            // clean reading of it is not evidence of anything. Recorded, so the project
            // rebuilds the moment the directory becomes readable and the fingerprint
            // changes shape.
            return new[] { new ProjectInput(BuildFileKind, Relative(root, directory), Unreadable) };
        }

        return entries
            .Where(path => BuildFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new ProjectInput(BuildFileKind, Relative(root, path), HashOfFile(path)))
            .ToList();
    }

    private static string HashOfText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// SHA-256 of a file, streamed. A file that will not open is recorded as unreadable
    /// rather than skipped: an input nobody could read is not an input that has not
    /// changed.
    /// </summary>
    private static string HashOfFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable;
        }
    }

    /// <summary>
    /// A path as it is written in the ledger: relative to the root with '/' separators
    /// when it lies under the root, and unchanged when it does not. Same rule, and the
    /// same reason, as a document's relative_path in this index.
    /// </summary>
    private static string Relative(string root, string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // A reference can be a display name rather than a path - an in-memory compilation
        // reference is one - and Path.GetFullPath on it would resolve it against the
        // current directory, which is a different string in every process.
        if (!Path.IsPathRooted(path)) return path.Replace('\\', '/');

        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative)) return path.Replace('\\', '/');

        relative = relative.Replace('\\', '/');
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            ? path.Replace('\\', '/')
            : relative;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>
/// What the ledger recorded for one project the last time this index was built.
///
/// The schema and vela versions are on the row rather than beside the table because they
/// are facts about the RUN that wrote it, and an incremental rebuild writes some rows and
/// leaves others. A single value somewhere else would be the version of the most recent
/// run, which says nothing about the rows that run did not touch.
/// </summary>
public sealed record RecordedProject(
    string Project,
    string Fingerprint,
    int SchemaVersion,
    string VelaVersion,
    IReadOnlyList<string> References);

/// <summary>
/// One reason a project is missing code from the index, in the emitter's own words,
/// prefix and all.
/// </summary>
/// <param name="Project">
/// The project it is about, as <see cref="ProjectFingerprint.IdentityOf"/> names it, so a
/// project that is skipped on a later run can keep saying what it could not do.
/// </param>
public sealed record ProjectNote(string Project, string Note);

/// <summary>
/// Each project's own reasons for being incomplete, recorded against the PROJECT and not
/// against the run that found them.
///
/// <b>The hole this closes.</b> A project that will not compile is fingerprinted like any
/// other, so an incremental run can skip it. Its `compile-error:` note is produced fresh
/// by each harvest and by nothing else, so skipping the project meant the note was never
/// regenerated: the index stopped calling itself degraded while still holding an
/// incomplete picture of that project. A broken project that goes quiet is precisely the
/// failure vela exists to prevent, and it is worse than the one it replaces, because
/// nobody is looking.
///
/// <b>Why the note is kept rather than the project rebuilt.</b> Both were open. Keeping
/// the note is honest for the same reason the skip is: the fingerprint says nothing this
/// project compiles has changed, and the closure says nothing upstream of it has either,
/// so the compiler would produce the same diagnostics. Forcing a rebuild of every project
/// that has ever had a problem pays the full price for byte-identical output, and it makes
/// the DEGRADED index the one that never gets faster - which is backwards, because an
/// index you already know is incomplete is the one you re-run most often.
///
/// What it does not cover is a diagnostic that changes without any fingerprinted input
/// changing, which is the same hole <see cref="ProjectFingerprint.ReferenceKind"/> already
/// states: an assembly rebuilt at the same path and the same version. A full rebuild is
/// the answer to that, and it is the default.
/// </summary>
public static class ProjectNotes
{
    /// <summary>
    /// Replaces the notes of the projects this run looked at, and leaves every other
    /// project's alone.
    /// </summary>
    /// <param name="harvested">
    /// The projects the run actually harvested. Their rows are cleared whether or not
    /// they produced a note, because a project that has been FIXED has to be able to stop
    /// saying it is broken - a record only a failure can write is a record nobody can
    /// clear.
    /// </param>
    public static void Write(
        SqliteConnection db, IReadOnlyList<string> harvested, IReadOnlyList<ProjectNote> notes)
    {
        using var tx = db.BeginTransaction();

        using (var clear = db.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM project_note WHERE project = $p";
            clear.Parameters.Add("$p", SqliteType.Text);

            foreach (var project in harvested)
            {
                clear.Parameters["$p"].Value = project;
                clear.ExecuteNonQuery();
            }
        }

        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT OR REPLACE INTO project_note(project, note) VALUES ($p, $n)";
        insert.Parameters.Add("$p", SqliteType.Text);
        insert.Parameters.Add("$n", SqliteType.Text);

        foreach (var note in notes)
        {
            insert.Parameters["$p"].Value = note.Project;
            insert.Parameters["$n"].Value = note.Note;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Every note this index holds, ordered by project and then by note so the same index
    /// renders the same banner on every run and every machine (Constraint 1).
    /// </summary>
    public static IReadOnlyList<ProjectNote> Read(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT project, note FROM project_note ORDER BY project, note";
        using var reader = cmd.ExecuteReader();

        var notes = new List<ProjectNote>();
        while (reader.Read()) notes.Add(new ProjectNote(reader.GetString(0), reader.GetString(1)));
        return notes;
    }
}

/// <summary>
/// Which documents each project contributed to.
///
/// A document is keyed by the file a developer can open, and two projects can compile one
/// file - a linked file, a shared source directory, a wildcard reaching into a common
/// folder. The occurrences of every project that compiles it land in ONE document row, so
/// replacing that row on behalf of one project deletes the other's occurrences and nothing
/// puts them back. <see cref="RebuildPlan"/> reads this to pull every such project into
/// the rebuild.
///
/// It is also the only record of what a project USED to contribute, which is how a
/// document whose file has been deleted is removed: the fresh harvest cannot name a file
/// that is not there, so without this there would be nothing to match the stale row
/// against.
/// </summary>
public static class ProjectDocuments
{
    /// <summary>
    /// Replaces the documents recorded for the projects this run harvested. Cleared per
    /// project for the same reason the notes are: a project that no longer compiles a file
    /// has to be able to stop saying it does.
    /// </summary>
    public static void Write(
        SqliteConnection db,
        IReadOnlyList<string> harvested,
        IReadOnlyDictionary<string, IReadOnlyList<string>> documents)
    {
        using var tx = db.BeginTransaction();

        using (var clear = db.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM project_document WHERE project = $p";
            clear.Parameters.Add("$p", SqliteType.Text);

            foreach (var project in harvested)
            {
                clear.Parameters["$p"].Value = project;
                clear.ExecuteNonQuery();
            }
        }

        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT OR REPLACE INTO project_document(project, relative_path) VALUES ($p, $d)";
        insert.Parameters.Add("$p", SqliteType.Text);
        insert.Parameters.Add("$d", SqliteType.Text);

        foreach (var entry in documents.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            insert.Parameters["$p"].Value = entry.Key;
            foreach (var path in entry.Value)
            {
                insert.Parameters["$d"].Value = path;
                insert.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// What each project contributed to, ordered so the plan built from it is the same on
    /// every run and every machine (Constraint 1).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT project, relative_path FROM project_document ORDER BY project, relative_path";
        using var reader = cmd.ExecuteReader();

        var documents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var project = reader.GetString(0);
            if (!documents.TryGetValue(project, out var paths))
                documents[project] = paths = new List<string>();
            paths.Add(reader.GetString(1));
        }

        return documents.ToDictionary(
            entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value, StringComparer.Ordinal);
    }
}

/// <summary>
/// The ledger's home in the database: what each project was built from, written by the
/// indexing pass and read by the next one to work out what it can honestly skip.
/// </summary>
public static class ProjectInputs
{
    /// <summary>
    /// The build of vela that wrote a row: the version the assembly declares, and then the
    /// module version id of the binary that actually ran. A different build can emit
    /// different occurrences from identical source - the anchor rules, the dedup rules and
    /// the moniker grammar have all changed at least once - so rows written by another
    /// build are not evidence about what this one would produce.
    ///
    /// <b>Why the declared version is not enough, and why this is not a style choice.</b>
    /// This used to be the declared version alone. vela's csproj sets no Version or
    /// VersionPrefix, so that is <c>1.0.0.0</c> for every build vela has ever produced: a
    /// value every build shares is a fact about none of them, the fallback that reads it
    /// could never fire, and the reference and SKILL.md both advertised it as working. A
    /// change to the harvest that left the schema alone would have mixed two builds' output
    /// in one index with no signal at all.
    ///
    /// A hand-maintained version number would only move that hole: it is a promise somebody
    /// has to remember to keep on the day they change the moniker grammar, and the whole
    /// point of this row is to be right about that day. The module version id is the
    /// compiler's own identifier for one compiled module, so nobody has to remember
    /// anything. C# builds are deterministic, so recompiling unchanged source produces the
    /// same id and an ordinary rebuild of vela invalidates nothing; changing ANY line of
    /// vela produces a different one. That is a superset of "changed the harvest", and it
    /// errs towards a full rebuild, which is the direction that cannot be stale.
    ///
    /// The consequence is worth stating plainly, and the documentation states it: the first
    /// incremental run after upgrading vela falls back to a full rebuild, and says so.
    ///
    /// Twelve hex characters of it, because the whole value is printed in the sentence that
    /// explains the fallback to a person, and 48 bits is far more than is needed to tell
    /// two builds of one tool apart. Computed once, because it cannot change while the
    /// process is running and it is read on every project of every index.
    /// </summary>
    public static string VelaVersion { get; } = Identify();

    private static string Identify()
    {
        var assembly = typeof(ProjectInputs).Assembly;
        var declared = assembly.GetName().Version?.ToString() ?? "0.0.0";

        return declared + "+" + assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
    }

    /// <summary>
    /// Records what each project was built from, replacing whatever the same project
    /// recorded last time. Keyed by project, so there is no state in which two rows
    /// describe one project and nothing can say which is current.
    /// </summary>
    public static void Write(
        SqliteConnection db,
        IReadOnlyList<ProjectFingerprint> fingerprints,
        int schemaVersion,
        string velaVersion,
        DateTime builtAtUtc)
    {
        if (fingerprints.Count == 0) return;

        using var tx = db.BeginTransaction();

        using var upsert = db.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = """
            INSERT INTO project_input(
                project, name, fingerprint, inputs, schema_version, vela_version, built_at_utc)
            VALUES ($p, $n, $f, $i, $s, $v, $t)
            ON CONFLICT(project) DO UPDATE SET
                name           = excluded.name,
                fingerprint    = excluded.fingerprint,
                inputs         = excluded.inputs,
                schema_version = excluded.schema_version,
                vela_version   = excluded.vela_version,
                built_at_utc   = excluded.built_at_utc
            """;
        foreach (var name in new[] { "$p", "$n", "$f", "$v", "$t" })
            upsert.Parameters.Add(name, SqliteType.Text);
        upsert.Parameters.Add("$i", SqliteType.Integer);
        upsert.Parameters.Add("$s", SqliteType.Integer);

        // The rows a previous run of this same project left behind. Replacing the digest
        // without replacing them would leave the ledger saying the project still compiles
        // a file it does not, which is the failure the ledger exists to catch.
        using var clearInputs = db.CreateCommand();
        clearInputs.Transaction = tx;
        clearInputs.CommandText = "DELETE FROM project_input_document WHERE project = $p";
        clearInputs.Parameters.Add("$p", SqliteType.Text);

        using var clearReferences = db.CreateCommand();
        clearReferences.Transaction = tx;
        clearReferences.CommandText = "DELETE FROM project_input_reference WHERE project = $p";
        clearReferences.Parameters.Add("$p", SqliteType.Text);

        using var insertInput = db.CreateCommand();
        insertInput.Transaction = tx;
        insertInput.CommandText =
            "INSERT OR REPLACE INTO project_input_document(project, kind, path, content_hash) "
            + "VALUES ($p, $k, $x, $h)";
        foreach (var name in new[] { "$p", "$k", "$x", "$h" })
            insertInput.Parameters.Add(name, SqliteType.Text);

        using var insertReference = db.CreateCommand();
        insertReference.Transaction = tx;
        insertReference.CommandText =
            "INSERT OR REPLACE INTO project_input_reference(project, referenced) VALUES ($p, $r)";
        foreach (var name in new[] { "$p", "$r" })
            insertReference.Parameters.Add(name, SqliteType.Text);

        foreach (var fingerprint in fingerprints)
        {
            upsert.Parameters["$p"].Value = fingerprint.Project;
            upsert.Parameters["$n"].Value = fingerprint.Name;
            upsert.Parameters["$f"].Value = fingerprint.Fingerprint;
            upsert.Parameters["$i"].Value = fingerprint.Inputs.Count;
            upsert.Parameters["$s"].Value = schemaVersion;
            upsert.Parameters["$v"].Value = velaVersion;
            upsert.Parameters["$t"].Value = builtAtUtc.ToString("O");
            upsert.ExecuteNonQuery();

            clearInputs.Parameters["$p"].Value = fingerprint.Project;
            clearInputs.ExecuteNonQuery();
            clearReferences.Parameters["$p"].Value = fingerprint.Project;
            clearReferences.ExecuteNonQuery();

            foreach (var input in fingerprint.Inputs)
            {
                insertInput.Parameters["$p"].Value = fingerprint.Project;
                insertInput.Parameters["$k"].Value = input.Kind;
                insertInput.Parameters["$x"].Value = input.Path;
                insertInput.Parameters["$h"].Value = input.ContentHash;
                insertInput.ExecuteNonQuery();
            }

            foreach (var reference in fingerprint.References)
            {
                insertReference.Parameters["$p"].Value = fingerprint.Project;
                insertReference.Parameters["$r"].Value = reference;
                insertReference.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// Every project this index remembers being built from, ordered by project so a plan
    /// computed from it comes out the same way on every run and every machine
    /// (Constraint 1).
    /// </summary>
    public static IReadOnlyList<RecordedProject> Read(SqliteConnection db)
    {
        var references = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText =
                "SELECT project, referenced FROM project_input_reference ORDER BY project, referenced";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var project = reader.GetString(0);
                if (!references.TryGetValue(project, out var list))
                    references[project] = list = new List<string>();
                list.Add(reader.GetString(1));
            }
        }

        using var projects = db.CreateCommand();
        projects.CommandText =
            "SELECT project, fingerprint, schema_version, vela_version FROM project_input ORDER BY project";
        using var rows = projects.ExecuteReader();

        var recorded = new List<RecordedProject>();
        while (rows.Read())
        {
            var project = rows.GetString(0);
            recorded.Add(new RecordedProject(
                project,
                rows.GetString(1),
                rows.GetInt32(2),
                rows.GetString(3),
                references.TryGetValue(project, out var referenced)
                    ? referenced
                    : Array.Empty<string>()));
        }

        return recorded;
    }

    /// <summary>
    /// What one project was built from, file by file. Not needed to decide a rebuild -
    /// the digest settles that - and kept because the digest can only say THAT a project
    /// changed. On the day an answer is wrong, this is the difference between diagnosing
    /// it and guessing.
    /// </summary>
    public static IReadOnlyList<ProjectInput> ReadInputs(SqliteConnection db, string project)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT kind, path, content_hash FROM project_input_document WHERE project = $p "
            + "ORDER BY kind, path";
        cmd.Parameters.AddWithValue("$p", project);
        using var reader = cmd.ExecuteReader();

        var inputs = new List<ProjectInput>();
        while (reader.Read())
            inputs.Add(new ProjectInput(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        return inputs;
    }

    /// <summary>
    /// When each project was last built, for a reader who wants to know how old a skipped
    /// project's rows are. Parsed round-trip, so it means the same instant wherever it is
    /// read.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTime> ReadBuiltAt(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT project, built_at_utc FROM project_input ORDER BY project";
        using var reader = cmd.ExecuteReader();

        var built = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        while (reader.Read())
        {
            built[reader.GetString(0)] = DateTime.Parse(
                reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        return built;
    }
}
