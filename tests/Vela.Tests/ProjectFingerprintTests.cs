using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

/// <summary>
/// The ledger of what each project was built from.
///
/// Nothing in vela recorded that, so there was nothing an incremental rebuild could
/// compare a tree against. A fingerprint is the comparison: it must be identical when
/// nothing has changed and different when anything the compiler reads has, because a
/// fingerprint that matches when the code has moved on is the silent-staleness failure
/// with a green light on top of it.
/// </summary>
public class ProjectFingerprintTests
{
    [Fact]
    public async Task ForAsync_IsStableAcrossTwoRunsOverAnUnchangedTree()
    {
        // The property everything else rests on. If two reads of one unchanged tree
        // disagree, every project is rebuilt on every run and the feature is pointless;
        // worse, nobody could tell that apart from a real change, so the signal would
        // mean nothing in either direction.
        using var fx = FixtureSolution.CreateWebApp();
        var root = ProjectRoot.ForSolution(fx.SolutionPath);

        var first = await FingerprintAsync(fx.SolutionPath, root);
        var second = await FingerprintAsync(fx.SolutionPath, root);

        Assert.NotEmpty(first);
        Assert.Equal(Identities(first), Identities(second));

        foreach (var project in first.Keys)
            Assert.Equal(first[project], second[project]);
    }

    [Fact]
    public async Task ForAsync_ChangesWhenARazorViewChanges()
    {
        // The load-bearing case, and the one that decides what a source-generated
        // document's input is. A .cshtml never reaches the compiler as a file: the Razor
        // generator turns it into a .g.cs that exists only in the compilation. So a
        // fingerprint that watched only the documents the compiler was handed on disk
        // would call a project unchanged after a view was rewritten, and every reference
        // vela reports from that view would describe the old one.
        using var fx = FixtureSolution.CreateWebApp();
        var root = ProjectRoot.ForSolution(fx.SolutionPath);

        var before = await FingerprintAsync(fx.SolutionPath, root);

        var view = Path.Combine(fx.Root, "App", "Pages", "Index.cshtml");
        Assert.True(File.Exists(view), view);
        File.AppendAllText(view, "\n<p>@ViewData[\"Title\"] once more</p>\n");

        var after = await FingerprintAsync(fx.SolutionPath, root);

        // The same projects, so the difference is in what they were built from and not
        // in which of them there are.
        Assert.Equal(Identities(before), Identities(after));
        Assert.All(before.Keys, project => Assert.NotEqual(before[project], after[project]));
    }

    [Fact]
    public void ForAsync_ChangesWhenASourceFilesContentChanges()
    {
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { public string Status = \"\"; }");

        var before = Fingerprint(tree, "src/App/Perfume.cs");

        tree.Write("src/App/Perfume.cs", "public class Perfume { public string Status = \"listed\"; }");

        Assert.NotEqual(before, Fingerprint(tree, "src/App/Perfume.cs"));
    }

    [Fact]
    public void ForAsync_DoesNotChangeWhenAFileIsOnlyTouched()
    {
        // Why this is a content hash and not a modification time. An mtime moves when
        // nothing did - a checkout, a `touch`, a `git stash pop` - and stands still when
        // something did, for a file restored with its timestamp preserved. The staleness
        // check can afford mtimes because it only has to raise a suspicion on every
        // query; a decision about what NOT to re-read has to be right, so it pays for a
        // read.
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");

        var before = Fingerprint(tree, "src/App/Perfume.cs");

        var later = DateTime.UtcNow.AddDays(1);
        File.SetLastWriteTimeUtc(Path.Combine(tree.Root, "src", "App", "Perfume.cs"), later);
        File.SetLastWriteTimeUtc(tree.ProjectFile, later);

        Assert.Equal(before, Fingerprint(tree, "src/App/Perfume.cs"));
    }

    [Fact]
    public void ForAsync_ChangesWhenAFileIsAdded()
    {
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");
        tree.Write("src/App/House.cs", "public class House { }");

        var before = Fingerprint(tree, "src/App/Perfume.cs");
        var after = Fingerprint(tree, "src/App/Perfume.cs", "src/App/House.cs");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ForAsync_ChangesWhenAFileIsRemoved()
    {
        // The direction that does the damage. A project that has lost a file still
        // compiles, and an index that kept the file's rows answers `def` with a path
        // nobody can open and `refs` with a count that includes code that is gone.
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");
        tree.Write("src/App/House.cs", "public class House { }");

        var before = Fingerprint(tree, "src/App/Perfume.cs", "src/App/House.cs");

        tree.Delete("src/App/House.cs");
        var after = Fingerprint(tree, "src/App/Perfume.cs");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ForAsync_ChangesWhenTheProjectFileItselfChanges()
    {
        // The project file decides which files are compiled at all, which packages they
        // see and which language version they are read under. Not one of its documents
        // need change for every symbol in the project to.
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");

        var before = Fingerprint(tree, "src/App/Perfume.cs");

        File.WriteAllText(tree.ProjectFile,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework><LangVersion>preview</LangVersion>"
            + "</PropertyGroup></Project>");

        Assert.NotEqual(before, Fingerprint(tree, "src/App/Perfume.cs"));
    }

    [Fact]
    public void ForAsync_ChangesWhenADirectoryBuildPropsAboveItChanges()
    {
        // A Directory.Build.props is imported into every project beneath it and is named
        // by none of them, so a project whose own files are all untouched can be
        // compiled under a different nullable setting, a different target framework or a
        // different set of implicit usings because of a file two directories up.
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");
        tree.Write("Directory.Build.props",
            "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");

        var before = Fingerprint(tree, "src/App/Perfume.cs");

        tree.Write("Directory.Build.props",
            "<Project><PropertyGroup><Nullable>disable</Nullable></PropertyGroup></Project>");

        Assert.NotEqual(before, Fingerprint(tree, "src/App/Perfume.cs"));
    }

    [Fact]
    public void ForAsync_ChangesWhenADirectoryBuildPropsAboveItAppears()
    {
        // The same file arriving where there was none. MSBuild starts importing it
        // without anything else on disk changing.
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");

        var before = Fingerprint(tree, "src/App/Perfume.cs");

        tree.Write("src/Directory.Build.props",
            "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");

        Assert.NotEqual(before, Fingerprint(tree, "src/App/Perfume.cs"));
    }

    [Fact]
    public async Task ForAsync_RecordsWhatTheProjectReferences()
    {
        // The closure the rebuild plan has to compute is only as good as the edges it is
        // given, and nothing else in the index records them.
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");
        tree.Write("src/Lib/Scent.cs", "public class Scent { }");

        var solution = tree.LoadPair();
        var root = ProjectRoot.ForSolutionDirectory(tree.Root);

        var app = solution.Projects.Single(p => p.Name == "App");
        var lib = solution.Projects.Single(p => p.Name == "Lib");

        var appPrint = await ProjectFingerprint.ForAsync(app, root, default);
        var libPrint = await ProjectFingerprint.ForAsync(lib, root, default);

        Assert.Equal(new[] { "src/Lib/Lib.csproj" }, appPrint.References);
        Assert.Empty(libPrint.References);

        // Identity is the project file relative to the root the index is built at, so it
        // is the same string on any machine and in any checkout directory.
        Assert.Equal("src/App/App.csproj", appPrint.Project);
        Assert.Equal("src/Lib/Lib.csproj", libPrint.Project);
    }

    [Fact]
    public async Task ForAsync_ChangesWhenAProjectReferenceIsAdded()
    {
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");
        tree.Write("src/Lib/Scent.cs", "public class Scent { }");

        var root = ProjectRoot.ForSolutionDirectory(tree.Root);

        var unreferenced = tree.LoadPair(referenced: false).Projects.Single(p => p.Name == "App");
        var referenced = tree.LoadPair(referenced: true).Projects.Single(p => p.Name == "App");

        Assert.NotEqual(
            (await ProjectFingerprint.ForAsync(unreferenced, root, default)).Fingerprint,
            (await ProjectFingerprint.ForAsync(referenced, root, default)).Fingerprint);
    }

    [Fact]
    public async Task ForAsync_NamesEveryDocumentItHashed()
    {
        // The digest alone says a project changed and never which file did it. The
        // per-document rows are what makes the claim auditable, which matters most on
        // the day the answer is wrong.
        using var tree = new Tree();
        tree.Write("src/App/Perfume.cs", "public class Perfume { }");

        var project = tree.Load("src/App/Perfume.cs");
        var print = await ProjectFingerprint.ForAsync(
            project, ProjectRoot.ForSolutionDirectory(tree.Root), default);

        Assert.Contains(print.Inputs, i => i.Path == "src/App/Perfume.cs" && i.Kind == ProjectFingerprint.SourceKind);
        Assert.Contains(print.Inputs, i => i.Path == "src/App/App.csproj" && i.Kind == ProjectFingerprint.BuildFileKind);

        // Content hashes, so two files holding the same text hash the same and one file
        // holding different text does not.
        var source = print.Inputs.Single(i => i.Path == "src/App/Perfume.cs");
        Assert.Matches("^[0-9a-f]{64}$", source.ContentHash);
    }

    [Fact]
    public async Task ForAsync_OrdersItsInputsTheSameWayEveryTime()
    {
        // Constraint 1. The digest is taken over this list, so an order that depended on
        // the filesystem would produce a different fingerprint for one unchanged tree on
        // a different machine, and every project would rebuild for no reason.
        using var tree = new Tree();
        tree.Write("src/App/B.cs", "public class B { }");
        tree.Write("src/App/A.cs", "public class A { }");
        tree.Write("src/App/C.cs", "public class C { }");

        var root = ProjectRoot.ForSolutionDirectory(tree.Root);

        var forwards = await ProjectFingerprint.ForAsync(
            tree.Load("src/App/A.cs", "src/App/B.cs", "src/App/C.cs"), root, default);
        var backwards = await ProjectFingerprint.ForAsync(
            tree.Load("src/App/C.cs", "src/App/B.cs", "src/App/A.cs"), root, default);

        Assert.Equal(forwards.Fingerprint, backwards.Fingerprint);
        Assert.Equal(
            forwards.Inputs.Select(i => i.Kind + " " + i.Path),
            backwards.Inputs.Select(i => i.Kind + " " + i.Path));
    }

    [Fact]
    public async Task EmitAsync_FingerprintsEveryProjectItHarvested()
    {
        // Recorded during the harvest, which already walks exactly these projects, so a
        // project cannot be indexed without also being written down.
        using var fx = FixtureSolution.CreateWebApp();
        var load = await WorkspaceLoader.LoadAsync(fx.SolutionPath, default);

        var emitted = await ScipEmitter.EmitAsync(load.Solution, load.Failures, default);

        Assert.Equal(
            load.Solution.Projects.Select(p => p.Name).Order(StringComparer.Ordinal),
            emitted.Fingerprints.Select(f => f.Name).Order(StringComparer.Ordinal));

        Assert.All(emitted.Fingerprints, f => Assert.Matches("^[0-9a-f]{64}$", f.Fingerprint));
    }

    [Fact]
    public void WriteThenRead_RoundTripsTheLedger()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        var fingerprints = new[]
        {
            new ProjectFingerprint("src/Web/Web.csproj", "Web", new string('b', 64),
                new[] { new ProjectInput(ProjectFingerprint.SourceKind, "src/Web/Home.cs", new string('1', 64)) },
                new[] { "src/Data/Data.csproj" }),
            new ProjectFingerprint("src/Data/Data.csproj", "Data", new string('a', 64),
                new[] { new ProjectInput(ProjectFingerprint.SourceKind, "src/Data/Perfume.cs", new string('2', 64)) },
                Array.Empty<string>())
        };

        ProjectInputs.Write(db, fingerprints, Schema.Version, "9.9.9.9", DateTime.UtcNow);

        var recorded = ProjectInputs.Read(db);

        // Ordered by project, ordinally, so a plan computed from it comes out the same
        // way on every run (Constraint 1).
        Assert.Equal(new[] { "src/Data/Data.csproj", "src/Web/Web.csproj" }, recorded.Select(r => r.Project));
        Assert.Equal(new string('a', 64), recorded[0].Fingerprint);
        Assert.Equal(new string('b', 64), recorded[1].Fingerprint);
        Assert.All(recorded, r => Assert.Equal(Schema.Version, r.SchemaVersion));
        Assert.All(recorded, r => Assert.Equal("9.9.9.9", r.VelaVersion));

        Assert.Equal(new[] { "src/Data/Data.csproj" }, recorded[1].References);
        Assert.Empty(recorded[0].References);

        // The documents each project compiled, with a hash each, which is what makes a
        // wrong decision diagnosable rather than merely wrong.
        Assert.Equal(
            new[] { "src/Data/Perfume.cs" },
            ProjectInputs.ReadInputs(db, "src/Data/Data.csproj").Select(i => i.Path));
    }

    [Fact]
    public void Write_ReplacesTheLedgerForAProjectRatherThanAddingASecondOne()
    {
        // There must be no state in which two rows describe one project and nothing can
        // say which is current, for the same reason imported_source is keyed by source.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        var first = new ProjectFingerprint("src/Data/Data.csproj", "Data", new string('a', 64),
            new[] { new ProjectInput(ProjectFingerprint.SourceKind, "src/Data/Perfume.cs", new string('1', 64)) },
            Array.Empty<string>());

        var second = first with
        {
            Fingerprint = new string('c', 64),
            Inputs = new[] { new ProjectInput(ProjectFingerprint.SourceKind, "src/Data/House.cs", new string('3', 64)) }
        };

        ProjectInputs.Write(db, new[] { first }, Schema.Version, "1.0.0.0", DateTime.UtcNow);
        ProjectInputs.Write(db, new[] { second }, Schema.Version, "1.0.0.0", DateTime.UtcNow);

        var recorded = Assert.Single(ProjectInputs.Read(db));
        Assert.Equal(new string('c', 64), recorded.Fingerprint);

        // And the documents of the run that has been replaced go with it, or a later
        // reader would be told the project still compiles a file it does not.
        Assert.Equal(
            new[] { "src/Data/House.cs" },
            ProjectInputs.ReadInputs(db, "src/Data/Data.csproj").Select(i => i.Path));
    }

    [Fact]
    public void Read_ReturnsNothingForAnIndexThatHasNeverBeenFingerprinted()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        Assert.Empty(ProjectInputs.Read(db));
    }

    private static IEnumerable<string> Identities(IReadOnlyDictionary<string, string> fingerprints) =>
        fingerprints.Keys.Order(StringComparer.Ordinal);

    /// <summary>Every project of a solution on disk, fingerprinted, keyed by identity.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> FingerprintAsync(string solutionPath, string root)
    {
        var load = await WorkspaceLoader.LoadAsync(solutionPath, default);
        Assert.Empty(load.Failures);

        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in load.Solution.Projects)
        {
            var print = await ProjectFingerprint.ForAsync(project, root, default);
            fingerprints[print.Project] = print.Fingerprint;
        }

        return fingerprints;
    }

    private static string Fingerprint(Tree tree, params string[] documents) =>
        ProjectFingerprint
            .ForAsync(tree.Load(documents), ProjectRoot.ForSolutionDirectory(tree.Root), default)
            .GetAwaiter().GetResult()
            .Fingerprint;

    /// <summary>
    /// A real directory tree with a real project file in it, read back the way a fresh
    /// workspace load would read it.
    ///
    /// Roslyn is not asked to build anything: the MSBuild files a fingerprint has to
    /// watch are watched by walking the disk, so what these tests need is files that
    /// exist and documents that point at them. That keeps a test of the ledger down to
    /// milliseconds, where scaffolding a real SDK project is tens of seconds, and leaves
    /// the two cases that genuinely need MSBuild - the unchanged tree and the edited
    /// Razor view - on the real fixture.
    /// </summary>
    private sealed class Tree : IDisposable
    {
        public string Root { get; }

        public string ProjectFile => Path.Combine(Root, "src", "App", "App.csproj");

        public Tree()
        {
            Root = Path.Combine(Path.GetTempPath(), "vela-fp-" + Guid.NewGuid().ToString("N")[..8]);

            // A .git entry is what ProjectRoot stops its walk at, so the root of this
            // tree is the root the fingerprint's paths are relative to.
            Directory.CreateDirectory(Path.Combine(Root, ".git"));

            Write("src/App/App.csproj", ProjectText);
            Write("src/Lib/Lib.csproj", ProjectText);
        }

        private const string ProjectText =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

        public void Write(string relativePath, string content)
        {
            var full = Full(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Delete(string relativePath) => File.Delete(Full(relativePath));

        /// <summary>The App project, compiling exactly the files named.</summary>
        public Project Load(params string[] documents) =>
            Solution(documents, Array.Empty<string>(), referenced: false).Projects.Single(p => p.Name == "App");

        /// <summary>App and Lib, with App referencing Lib unless told otherwise.</summary>
        public Solution LoadPair(bool referenced = true) =>
            Solution(new[] { "src/App/Perfume.cs" }, new[] { "src/Lib/Scent.cs" }, referenced);

        private Solution Solution(
            IReadOnlyList<string> appDocuments, IReadOnlyList<string> libDocuments, bool referenced)
        {
            var workspace = new AdhocWorkspace();

            var appId = ProjectId.CreateNewId();
            var libId = ProjectId.CreateNewId();

            var lib = Describe(libId, "Lib", "src/Lib/Lib.csproj", libDocuments, Array.Empty<ProjectReference>());
            var app = Describe(appId, "App", "src/App/App.csproj", appDocuments,
                referenced ? new[] { new ProjectReference(libId) } : Array.Empty<ProjectReference>());

            var solution = SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Default,
                filePath: Path.Combine(Root, "App.sln"),
                projects: libDocuments.Count == 0 ? new[] { app } : new[] { app, lib });

            return workspace.AddSolution(solution);
        }

        private ProjectInfo Describe(
            ProjectId id, string name, string projectFile,
            IReadOnlyList<string> documents, IReadOnlyList<ProjectReference> references)
        {
            var infos = documents.Select(relative =>
            {
                var full = Full(relative);
                return DocumentInfo.Create(
                    DocumentId.CreateNewId(id),
                    Path.GetFileName(full),
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(File.ReadAllText(full)), VersionStamp.Default)),
                    filePath: full);
            }).ToList();

            return ProjectInfo.Create(
                    id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                    filePath: Full(projectFile),
                    documents: infos,
                    projectReferences: references,
                    metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) })
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        private string Full(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
