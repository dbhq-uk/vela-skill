using Microsoft.Data.Sqlite;
using Vela.Harvest;
using Vela.Indexing;
using Vela.Tests.Fixtures;
using Xunit;

// Two tests in this class point XDG_CACHE_HOME at a fixture directory, and Task 8's
// CLI tests do the same. An environment variable is process-wide, so those classes
// share a collection with parallelisation disabled and can never overlap.
[Collection(EnvironmentSensitive.Name)]
public class ScipLoaderTests : IClassFixture<HarvestedWebApp>
{
    private readonly HarvestedWebApp _webApp;

    public ScipLoaderTests(HarvestedWebApp webApp) => _webApp = webApp;

    [Fact]
    public void Load_PopulatesDocumentsAndOccurrences()
    {
        var index = _webApp.Emitted.Index;

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        Assert.Equal(index.Documents.Count, ScalarInt(db, "SELECT COUNT(*) FROM document"));
        Assert.True(ScalarInt(db, "SELECT COUNT(*) FROM occurrence") > 0);
    }

    [Fact]
    public void Schema_CreatesAnFts5SymbolIndex()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ('Perfume.Status')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_fts WHERE symbol_fts MATCH 'Status'";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void IndexPaths_ResolvesUnderTheCacheDirectoryAndDependsOnTheSolutionPath()
    {
        // Constraint 2/3: indexing must not write into the repository. The previous
        // version of this test only asserted the resolved path did not start with a
        // fresh /tmp/vela-fx-* fixture root, which is true of almost any path
        // (including a hardcoded constant that ignores solutionPath entirely) and
        // never actually exercised XDG_CACHE_HOME. This version pins the cache root
        // to a known location and checks the path is genuinely derived from
        // solutionPath: two different solutions resolve to two different paths, and
        // the same solution resolves to the same path twice (it is a content hash).
        //
        // Fixtures are created before the environment mutation so only the direct,
        // in-process calls to IndexPaths.ForSolution - a pure function with no I/O -
        // run inside the try block. That keeps the window in which XDG_CACHE_HOME is
        // overridden as small as possible. The other tests in this assembly that
        // read or set XDG_CACHE_HOME (Task 8's CLI tests) sit in the same
        // non-parallel collection as this class, so none of them can run while this
        // override is in place, and the previous value is restored in a finally
        // block so a failure here cannot leak state into any test that runs
        // afterwards.
        using var fxA = FixtureSolution.CreateEmptySolution();
        using var fxB = FixtureSolution.CreateEmptySolution();

        var cacheRoot = Path.Combine(Path.GetTempPath(), "vela-cache-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string pathA1, pathA2, pathB;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cacheRoot);
            pathA1 = IndexPaths.ForSolution(fxA.SolutionPath);
            pathA2 = IndexPaths.ForSolution(fxA.SolutionPath);
            pathB = IndexPaths.ForSolution(fxB.SolutionPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
        }

        // Resolved the way IndexPaths resolves it, because on macOS Path.GetTempPath is
        // reached through /var, which is a link to /private/var, and GetFullPath does not
        // follow links. Still the same assertion: the index for a solution lives under
        // the cache directory and nowhere else.
        var fullCacheRoot = RealPath.Of(cacheRoot);
        Assert.StartsWith(fullCacheRoot + Path.DirectorySeparatorChar, pathA1, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pathA1, pathA2);
        Assert.NotEqual(pathA1, pathB);
    }

    [Fact]
    public void ForSolution_GuardsAgainstTheCacheDirectoryLandingInsideTheSolution()
    {
        // Finding 2: if the resolved cache directory is inside the repository being
        // indexed, vela must refuse loudly rather than silently write the index into
        // the repository it is indexing (Constraint 2). This must not be fooled by a
        // sibling directory that merely shares a string prefix with the solution
        // root (e.g. "/repo-backup" against "/repo").
        using var fx = FixtureSolution.CreateEmptySolution();
        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            // Exactly the solution directory, with a trailing separator: must be
            // caught, not just a strict subdirectory.
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", fx.Root + Path.DirectorySeparatorChar);
            var exactMatch = Assert.Throws<InvalidOperationException>(() => IndexPaths.ForSolution(fx.SolutionPath));
            Assert.Contains("XDG_CACHE_HOME", exactMatch.Message);

            // A subdirectory nested under the solution directory: must also be caught.
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(fx.Root, "nested", "cache"));
            var nested = Assert.Throws<InvalidOperationException>(() => IndexPaths.ForSolution(fx.SolutionPath));
            Assert.Contains("XDG_CACHE_HOME", nested.Message);

            // A sibling directory that only shares a string prefix must NOT be caught.
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", fx.Root.TrimEnd(Path.DirectorySeparatorChar) + "-backup");
            var siblingPath = IndexPaths.ForSolution(fx.SolutionPath);
            Assert.False(string.IsNullOrEmpty(siblingPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
        }
    }

    [Fact]
    public void Load_ThrowsWhenTheDatabaseAlreadyContainsData()
    {
        // Finding 3: ScipLoader.Load requires an empty schema. Schema.Create uses
        // CREATE TABLE IF NOT EXISTS and never truncates, so re-running Load against
        // a database that already holds an index - a normal re-index - must fail
        // with an intelligible message rather than a raw SqliteException from the
        // relative_path UNIQUE constraint.
        var index = MinimalIndex();

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        var ex = Assert.Throws<InvalidOperationException>(() => ScipLoader.Load(db, index));
        Assert.Contains("delete the file and re-index", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalDocumentPaths_RecoverWhichFilesWereSkipped_NotJustHowMany()
    {
        // A count with no way to see what it counted is not diagnosable. The paths
        // existed only in the emitted index's tool arguments, which are never persisted
        // and never written out as SCIP, so once `vela index` had printed "1
        // document(s) ... were not indexed" nothing could say which one. If the
        // classification is ever wrong about a file, that is the difference between a
        // five-second check and a hole in the index nobody can explain.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ToolInfo = new Scip.ToolInfo { Name = "vela", Version = "0.0.0" } }
        };

        index.Metadata.ToolInfo.Arguments.Add("external-document: /cache/microsoft.net.test.sdk/Program.cs");
        index.Metadata.ToolInfo.Arguments.Add("external-document: /dotnet/shared/Framework.cs");

        // The degrading channel is a different question with a different answer, and it
        // already reaches the reader through the banner.
        index.Metadata.ToolInfo.Arguments.Add("outside-project-root: /elsewhere/Shared/Foo.cs");
        index.Metadata.ToolInfo.Arguments.Add("load-failure: App.csproj did not load");

        var paths = Program.ExternalDocumentPaths(index);

        Assert.Equal(
            new[] { "/cache/microsoft.net.test.sdk/Program.cs", "/dotnet/shared/Framework.cs" },
            paths);

        // The number `vela index` prints is derived from this same list, so the count
        // and the list can never disagree.
        Assert.Equal(paths.Count, Program.CountExternalDocuments(index));
    }

    [Fact]
    public void ExternalDocuments_ArePersistedWithTheIndexAndNamedByStats()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        // Nothing was skipped, so nothing is said. A stock solution's --stats output is
        // exactly what it was, which is what AGENTS.md documents as the coverage check.
        var quiet = IndexStatistics.Render(IndexStatistics.Read(db));
        Assert.DoesNotContain("external", quiet, StringComparison.OrdinalIgnoreCase);

        var skipped = new[]
        {
            "/home/dev/.nuget/packages/microsoft.net.test.sdk/18.4.0/build/net8.0/Microsoft.NET.Test.Sdk.Program.cs",
            "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/Generated.cs"
        };

        ExternalDocuments.Write(db, skipped);

        // Persisted, so the answer outlives the process that worked it out.
        var stats = IndexStatistics.Read(db);
        Assert.Equal(skipped, stats.ExternalDocuments);

        var rendered = IndexStatistics.Render(stats);
        Assert.Contains("external documents", rendered, StringComparison.Ordinal);
        foreach (var path in skipped)
            Assert.Contains(path, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_StoresBothNamesForEveryOccurrence()
    {
        // The whole point of storing two names. symbol is the display name the query
        // layer matches on, and scip_symbol is the moniker that makes the index
        // exportable and correlatable. Overwriting either with the other loses
        // something no other column holds.
        var emitted = _webApp.Emitted;

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, emitted);

        // scip.proto makes Occurrence.symbol optional, and vela leaves it empty rather
        // than claiming a `local` for something that is not local to the document. The
        // loader has to carry that through exactly: as many empty monikers as the
        // emitter produced, no more and no fewer, so neither a lost moniker nor an
        // invented one can hide in the column.
        var absent = emitted.Index.Documents
            .SelectMany(d => d.Occurrences)
            .Count(o => o.Symbol.Length == 0);

        Assert.True(absent > 0, "the fixture spells `dynamic` and the global namespace, so this path is exercised");
        Assert.Equal(absent, ScalarInt(db, "SELECT COUNT(*) FROM occurrence WHERE scip_symbol = ''"));

        // A display name is a dotted Roslyn string and a moniker is not, so a column
        // that had quietly been filled with the other one would show here.
        Assert.Equal(0, ScalarInt(db,
            "SELECT COUNT(*) FROM occurrence WHERE symbol LIKE 'scip-dotnet %' OR symbol LIKE 'local %'"));
        Assert.True(ScalarInt(db,
            "SELECT COUNT(*) FROM occurrence WHERE scip_symbol LIKE 'scip-dotnet nuget %'") > 0);

        // The full-text index is what `find` searches, and a person types a name.
        Assert.Equal(0, ScalarInt(db, "SELECT COUNT(*) FROM symbol_fts WHERE symbol LIKE 'scip-dotnet %'"));

        // The symbol the end-to-end test asks for by name, with both of its names.
        var pair = ReadOne(db,
            "SELECT symbol, scip_symbol FROM occurrence WHERE symbol LIKE '%.ViewData' LIMIT 1");
        Assert.NotNull(pair);
        Assert.StartsWith("scip-dotnet nuget ", pair!.Value.Scip, StringComparison.Ordinal);
        Assert.EndsWith("ViewData.", pair.Value.Scip, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_KeepsAForeignSymbolAsItsOwnDisplayName()
    {
        // An index read from another tool's .scip file has one name, theirs. Storing
        // an empty display name for it would make it unfindable, so the SCIP symbol
        // stands in for both.
        var index = MinimalIndex();

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index);

        var pair = ReadOne(db, "SELECT symbol, scip_symbol FROM occurrence LIMIT 1");
        Assert.NotNull(pair);
        Assert.Equal("scip-csharp cargo Foo 1.0.0 Foo#", pair!.Value.Display);
        Assert.Equal("scip-csharp cargo Foo 1.0.0 Foo#", pair.Value.Scip);
    }

    [Fact]
    public void Load_StoresAnOccurrenceThatCarriesNoScipSymbol()
    {
        // Occurrence.symbol is optional in scip.proto, and vela now leaves it empty for
        // an array, a pointer, `dynamic` and the global namespace rather than asserting
        // a document scope none of them has. The column is NOT NULL DEFAULT '', so the
        // row is storable, but nothing had ever stored one: this is that path.
        var index = new Scip.Index();
        var doc = new Scip.Document { RelativePath = "Foo.cs", Language = "csharp" };
        doc.Occurrences.Add(new Scip.Occurrence { SymbolRoles = 0 });
        index.Documents.Add(doc);

        var occurrence = doc.Occurrences[0];
        var displayNames = new Dictionary<Scip.Occurrence, string>(ReferenceEqualityComparer.Instance)
        {
            [occurrence] = "string[]"
        };

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, index, null, displayNames);

        var pair = ReadOne(db, "SELECT symbol, scip_symbol FROM occurrence LIMIT 1");
        Assert.NotNull(pair);

        // The display name is the only name it has, and it is the one the query layer
        // reads, so the row is as findable as any other.
        Assert.Equal("string[]", pair!.Value.Display);
        Assert.Equal("", pair.Value.Scip);
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM symbol_fts WHERE symbol = 'string[]'"));
    }

    [Fact]
    public void LoadIncremental_ReplacesTheDocumentsItIsGivenAndLeavesTheRestAlone()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, TwoDocuments());

        // A second harvest of one project only: One.cs has lost a symbol and gained
        // another, and Two.cs was not looked at.
        var replacement = new Scip.Index();
        replacement.Documents.Add(Document("One.cs", "One.After"));

        var result = ScipLoader.LoadIncremental(
            db, new Vela.Harvest.EmitResult(replacement, new HashSet<string>()), new[] { "One.cs" });

        Assert.Equal(1, result.DocumentsRemoved);
        Assert.Equal(1, result.DocumentsWritten);
        Assert.Empty(result.SkippedPaths);

        Assert.Equal(2, ScalarInt(db, "SELECT COUNT(*) FROM document"));
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM occurrence WHERE symbol = 'One.After'"));
        Assert.Equal(0, ScalarInt(db, "SELECT COUNT(*) FROM occurrence WHERE symbol = 'One.Before'"));

        // The document nobody rebuilt still answers, which is the whole claim the
        // feature makes.
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM occurrence WHERE symbol = 'Two.Only'"));

        // And nothing in the full-text table names a symbol no occurrence carries. It is
        // keyed to nothing, so a name that has gone survives unless it is removed.
        Assert.Equal(0, ScalarInt(db,
            "SELECT COUNT(*) FROM symbol_fts WHERE symbol NOT IN (SELECT symbol FROM occurrence)"));
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM symbol_fts WHERE symbol = 'One.After'"));
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM symbol_fts WHERE symbol = 'Two.Only'"));
    }

    [Fact]
    public void LoadIncremental_DeletesADocumentThatIsNoLongerProduced()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, TwoDocuments());

        // The project rebuilt and produced nothing at all for One.cs, which is what a
        // deleted file looks like. Leaving the row would be the index describing a file
        // that is not there.
        var result = ScipLoader.LoadIncremental(
            db, new Vela.Harvest.EmitResult(new Scip.Index(), new HashSet<string>()), new[] { "One.cs" });

        Assert.Equal(1, result.DocumentsRemoved);
        Assert.Equal(0, result.DocumentsWritten);
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM document"));
        Assert.Equal(0, ScalarInt(db, "SELECT COUNT(*) FROM occurrence WHERE symbol = 'One.Before'"));
        Assert.Equal(0, ScalarInt(db, "SELECT COUNT(*) FROM symbol_fts WHERE symbol = 'One.Before'"));
    }

    [Fact]
    public void LoadIncremental_NeverTouchesADocumentAnImportContributed()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        ScipLoader.Load(db, TwoDocuments());

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(relative_path, language, generated, position_encoding, source)
                VALUES ('site/app.ts', 'typescript', 0, 0, '/tmp/site.scip');
                INSERT INTO occurrence(document_id, symbol, scip_symbol, is_definition, start_line, start_char)
                VALUES ((SELECT id FROM document WHERE relative_path = 'site/app.ts'),
                        'renderPage', 'scip-typescript npm app 1.0.0 `app.ts`/renderPage().', 1, 3, 9);
                INSERT INTO symbol_fts(symbol) VALUES ('renderPage');
                """;
            cmd.ExecuteNonQuery();
        }

        var replacement = new Scip.Index();
        replacement.Documents.Add(Document("One.cs", "One.After"));

        // A full rebuild deletes the database and replays every import into the new one.
        // An incremental rebuild never deletes the database, so an import survives by not
        // being touched - including when the paths handed in name it.
        ScipLoader.LoadIncremental(
            db,
            new Vela.Harvest.EmitResult(replacement, new HashSet<string>()),
            new[] { "One.cs", "site/app.ts" });

        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM document WHERE source = '/tmp/site.scip'"));
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM occurrence WHERE symbol = 'renderPage'"));
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM symbol_fts WHERE symbol = 'renderPage'"));
    }

    [Fact]
    public void LoadIncremental_RefusesToWriteOverADocumentAnImportOwnsAndNamesIt()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(relative_path, language, generated, position_encoding, source)
                VALUES ('One.cs', 'csharp', 0, 0, '/tmp/other.scip')
                """;
            cmd.ExecuteNonQuery();
        }

        var replacement = new Scip.Index();
        replacement.Documents.Add(Document("One.cs", "One.After"));

        var result = ScipLoader.LoadIncremental(
            db, new Vela.Harvest.EmitResult(replacement, new HashSet<string>()), Array.Empty<string>());

        // relative_path is UNIQUE, so writing it would abort the whole rebuild. The row
        // is left where it is and the collision is named, because a document quietly not
        // written is the shape of failure this codebase forbids.
        Assert.Equal(new[] { "One.cs" }, result.SkippedPaths);
        Assert.Equal(0, result.DocumentsWritten);
        Assert.Equal(1, ScalarInt(db, "SELECT COUNT(*) FROM document WHERE source = '/tmp/other.scip'"));
    }

    private static Scip.Index TwoDocuments()
    {
        var index = new Scip.Index();
        index.Documents.Add(Document("One.cs", "One.Before"));
        index.Documents.Add(Document("Two.cs", "Two.Only"));
        return index;
    }

    private static Scip.Document Document(string path, string symbol)
    {
        var doc = new Scip.Document { RelativePath = path, Language = "csharp" };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = symbol,
            SymbolRoles = (int)Scip.SymbolRole.Definition
        });
        return doc;
    }

    private static (string Display, string Scip)? ReadOne(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static Scip.Index MinimalIndex()
    {
        var index = new Scip.Index();
        var doc = new Scip.Document { RelativePath = "Foo.cs", Language = "csharp" };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = "scip-csharp cargo Foo 1.0.0 Foo#",
            SymbolRoles = (int)Scip.SymbolRole.Definition
        });
        index.Documents.Add(doc);
        return index;
    }

    private static int ScalarInt(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
