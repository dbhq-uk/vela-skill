using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vela.Indexing;
using Xunit;

namespace Vela.Tests;

public class ScipImporterTests
{
    // ================================================================================
    // The display name derivation.
    //
    // A foreign index gives vela one name for a symbol, its SCIP moniker, and the query
    // layer matches on a dotted display name. So every imported row's display name is
    // derived from the moniker's descriptors, and the rule has to hold against the
    // grammars real indexers actually emit rather than against the one vela emits.
    // ================================================================================

    [Theory]
    // Read out of an index scip-typescript 0.4.0 actually produced, over
    // /home/devops/scentverdict/src/ScentVerdict.Mobile. Not written by hand: the
    // module descriptors carry the file extension, which is what makes them the
    // interesting case, and no test author would have invented `'vue'` as an identifier.
    [InlineData("scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/formatErrorMessage().",
                "src.composables.useApi.ts.formatErrorMessage")]
    [InlineData("scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/formatErrorMessage().(error)",
                "src.composables.useApi.ts.formatErrorMessage.error")]
    [InlineData("scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/useAsyncData().[T]",
                "src.composables.useApi.ts.useAsyncData.T")]
    [InlineData("scip-typescript npm @vue/reactivity 3.5.32 dist/`reactivity.d.ts`/Ref#",
                "dist.reactivity.d.ts.Ref")]
    [InlineData("scip-typescript npm typescript 5.9.3 lib/`lib.es5.d.ts`/Error#message.",
                "lib.lib.es5.d.ts.Error.message")]
    [InlineData("scip-typescript npm vue-router 5.0.4 dist/`index-BzEKChPW.d.ts`/`'vue'`/",
                "dist.index-BzEKChPW.d.ts.'vue'")]
    [InlineData("scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/",
                "src.composables.useApi.ts")]

    // vela's own grammar, which is scip-dotnet's. The generic arity is the one place a
    // descriptor name is not the name a person reads.
    [InlineData("scip-dotnet nuget App 1.0.0.0 App/Pages/IndexModel#OnGet().",
                "App.Pages.IndexModel.OnGet")]
    [InlineData("scip-dotnet . . . Microsoft/AspNetCore/", "Microsoft.AspNetCore")]
    [InlineData("scip-dotnet nuget Microsoft.Extensions.Logging.Abstractions 10.0.0.0 Microsoft/Extensions/Logging/`ILogger``1`#",
                "Microsoft.Extensions.Logging.ILogger")]
    [InlineData("scip-dotnet nuget App 1.0.0.0 App/Svc#Publish(+1).", "App.Svc.Publish")]
    [InlineData("scip-dotnet nuget App 1.0.0.0 App/Svc#Publish().(item)", "App.Svc.Publish.item")]

    // scip.proto's own worked example of a symbol, and the shapes the other indexers in
    // the ecosystem emit for the same construct.
    [InlineData("scip-java maven com.example:lib 1.0.0 com/example/MyClass#myMethod(+1).",
                "com.example.MyClass.myMethod")]
    [InlineData("scip-go gomod github.com/foo/bar v1.0.0 pkg/util/Helper#Do().", "pkg.util.Helper.Do")]
    [InlineData("scip-python python mypkg 1.0 mymodule/MyClass#my_method().", "mymodule.MyClass.my_method")]
    [InlineData("rust-analyzer cargo mycrate 0.1.0 mymod/MyStruct#method().", "mymod.MyStruct.method")]

    // A space inside a package part is escaped by doubling it, so the part boundaries
    // are not where a naive split on ' ' would put them.
    [InlineData("scip-dotnet nuget My  Lib 1.0 A/B#", "A.B")]

    // The two suffixes nothing in .NET produces: meta and macro.
    [InlineData("scip-x . . . a/b:", "a.b")]
    [InlineData("rust-analyzer cargo c 1.0 m/vec!", "m.vec")]
    public void DisplayName_IsTheDescriptorNamesJoinedByDots(string moniker, string expected) =>
        Assert.Equal(expected, MonikerName.For(moniker));

    [Theory]
    // Nothing here parses as a moniker, and an invented name would be worse than the
    // string the index actually carries: their symbol is the only name it has.
    [InlineData("")]
    [InlineData("not a moniker at all")]
    [InlineData("scip-dotnet nuget App 1.0.0.0 ")]
    [InlineData("scip-dotnet nuget App 1.0.0.0 App/Unterminated`name")]
    [InlineData("local 4")]
    public void DisplayName_FallsBackToTheMonikerWhenItCannotBeParsed(string moniker) =>
        Assert.Equal(moniker, MonikerName.For(moniker));

    // ================================================================================
    // The schema
    // ================================================================================

    [Fact]
    public void Schema_RecordsAPositionEncodingPerDocument()
    {
        using var db = Fresh();

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('document') WHERE name = 'position_encoding'";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Schema_IndexesScipSymbol()
    {
        // 23,200 occurrences of the real solution carry the empty-string sentinel in
        // this column, and any join on it has to exclude them. An unindexed column is
        // also a full scan of 935,029 rows for anyone correlating two indexes through
        // it, which is the only reason the column exists.
        using var db = Fresh();

        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'occurrence' "
            + "AND sql LIKE '%scip_symbol%'";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    // ================================================================================
    // Hazard 1: position encoding
    // ================================================================================

    [Fact]
    public void Import_NormalisesEveryPositionToUtf16CodeUnits()
    {
        // scip.proto tells an indexer to choose its encoding by implementation language,
        // so one merged index legitimately holds all three. The example is the schema's
        // own: for "🚀 Woo" the offset of 'W' is 5 in UTF-8 bytes, 3 in UTF-16 code
        // units and 2 in UTF-32 code points. All three name the same character, so all
        // three must be stored as the same number, and that number is the UTF-16 one
        // because that is what every row vela has ever written already means.
        const string line = "\U0001F680 Woo";

        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };

        index.Documents.Add(DocumentWithOccurrenceAt(
            "utf8.go", line, Scip.PositionEncoding.Utf8CodeUnitOffsetFromLineStart, 5));
        index.Documents.Add(DocumentWithOccurrenceAt(
            "utf16.ts", line, Scip.PositionEncoding.Utf16CodeUnitOffsetFromLineStart, 3));
        index.Documents.Add(DocumentWithOccurrenceAt(
            "utf32.py", line, Scip.PositionEncoding.Utf32CodeUnitOffsetFromLineStart, 2));

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");

        var stored = Column(db, "SELECT o.start_char FROM occurrence o JOIN document d ON d.id = o.document_id ORDER BY d.relative_path");
        Assert.Equal(new[] { 3, 3, 3 }, stored);
    }

    [Fact]
    public void Import_RecordsTheEncodingEachDocumentDeclared()
    {
        // The numbers are normalised, so the declared encoding is not how to read them.
        // It is recorded so that the conversion is auditable and so an export can put
        // the document back the way its indexer wrote it.
        const string line = "\U0001F680 Woo";

        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        index.Documents.Add(DocumentWithOccurrenceAt(
            "utf8.go", line, Scip.PositionEncoding.Utf8CodeUnitOffsetFromLineStart, 5));
        index.Documents.Add(DocumentWithOccurrenceAt(
            "utf32.py", line, Scip.PositionEncoding.Utf32CodeUnitOffsetFromLineStart, 2));

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");

        // Ordered by path, so utf32.py sorts before utf8.go.
        Assert.Equal(
            new[] { (int)Scip.PositionEncoding.Utf32CodeUnitOffsetFromLineStart,
                    (int)Scip.PositionEncoding.Utf8CodeUnitOffsetFromLineStart },
            Column(db, "SELECT position_encoding FROM document ORDER BY relative_path"));
    }

    [Fact]
    public void Import_ReportsDocumentsWhoseOffsetsCouldNotBeConverted()
    {
        // No text in the index and no file on disk, so there is no line to count
        // characters of and the offsets are stored as they arrived. That is a claim
        // about the index which is wrong on any line holding a non-ASCII character, so
        // it is reported rather than assumed away (Constraint 3).
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        var doc = DocumentWithOccurrenceAt(
            "gone.py", "", Scip.PositionEncoding.Utf32CodeUnitOffsetFromLineStart, 2);
        doc.Text = "";
        index.Documents.Add(doc);

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo");

        Assert.Equal(1, report.UnconvertedDocuments);
    }

    // ================================================================================
    // Hazard 2: local symbols are document-scoped
    // ================================================================================

    [Fact]
    public void Import_KeepsTheSameLocalIdInTwoDocumentsDistinct()
    {
        // scip.proto: "Local symbols MUST only be used for entities which are local to
        // a Document, and cannot be accessed from outside the Document." So `local 1`
        // in one file and `local 1` in another are unrelated, and vela keys occurrences
        // by symbol string globally. Four documents of the real scip-typescript index
        // over ScentVerdict.Mobile each carry a `local 1`; without namespacing, refs on
        // one would answer with all four.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };

        foreach (var path in new[] { "a.ts", "b.ts" })
        {
            var doc = new Scip.Document { RelativePath = path, Language = "typescript" };
            doc.Occurrences.Add(new Scip.Occurrence
            {
                Symbol = "local 1",
                SymbolRoles = (int)Scip.SymbolRole.Definition,
                Range = { 0, 4, 9 }
            });
            index.Documents.Add(doc);
        }

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");

        Assert.Equal(2, Convert.ToInt32(Scalar(db, "SELECT COUNT(DISTINCT symbol) FROM occurrence")));

        // The moniker itself is left exactly as the index wrote it. It is theirs, it is
        // what an export has to put back, and the spec already forbids reaching it from
        // outside the document, so it is the display name that carries the namespacing.
        Assert.Equal(1, Convert.ToInt32(Scalar(db, "SELECT COUNT(DISTINCT scip_symbol) FROM occurrence")));
    }

    [Fact]
    public void Import_NamesALocalAfterTheSymbolThatEnclosesIt()
    {
        // scip.proto gives SymbolInformation.enclosing_symbol precisely so a local can
        // be placed in a hierarchy, and vela's own emitter fills it in. Where it is
        // there, the local is named the way vela names a C# local - the container, then
        // the short name - so `refs count` reaches it.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };

        var doc = new Scip.Document { RelativePath = "App/Counter.cs", Language = "csharp" };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = "local 7",
            SymbolRoles = (int)Scip.SymbolRole.Definition,
            Range = { 3, 12, 17 }
        });
        doc.Symbols.Add(new Scip.SymbolInformation
        {
            Symbol = "local 7",
            DisplayName = "count",
            EnclosingSymbol = "scip-dotnet nuget App 1.0.0.0 App/Counter#First()."
        });
        index.Documents.Add(doc);

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");

        Assert.Equal(
            "App/Counter.cs#App.Counter.First.count",
            Scalar(db, "SELECT symbol FROM occurrence")?.ToString());
    }

    // ================================================================================
    // Hazard 3: project roots differ
    // ================================================================================

    [Fact]
    public void Import_RebasesEveryPathOntoVelasOwnRoot()
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                ProjectRoot = new Uri("/repo/src/Mobile/").AbsoluteUri
            }
        };
        index.Documents.Add(new Scip.Document { RelativePath = "src/api.ts", Language = "typescript" });

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");

        Assert.Equal("src/Mobile/src/api.ts", Scalar(db, "SELECT relative_path FROM document")?.ToString());
    }

    [Fact]
    public void Import_RecordsAPathItCannotRebaseRatherThanDroppingIt()
    {
        // Constraint 3. A document whose file lies outside the root vela is indexing
        // cannot be given a path any reader could open, and scip.proto forbids a
        // relative_path with '..' in it, so it cannot be a document of this index. What
        // it must not be is absent without a word.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/elsewhere/").AbsoluteUri }
        };
        index.Documents.Add(new Scip.Document { RelativePath = "Shared/Thing.go", Language = "go" });

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo");

        Assert.Equal(0, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));
        Assert.Contains(report.Problems, p => p.Contains("Shared/Thing.go", StringComparison.Ordinal));
        Assert.True(report.Degraded);
    }

    // ================================================================================
    // What an import is, as opposed to a load
    // ================================================================================

    [Fact]
    public void Import_AddsToAnIndexThatAlreadyHoldsOne()
    {
        // ScipLoader.Load is a one-shot bulk load and refuses a non-empty schema. The
        // whole point of import is the opposite: the C# index is already there and a
        // second language is being added beside it.
        var first = SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/one().");
        var second = SingleDocumentIndex("b.ts", "scip-typescript npm p 1.0 src/`b.ts`/two().");

        using var db = Fresh();
        ScipImporter.Import(db, first, "/repo");
        ScipImporter.Import(db, second, "/repo");

        Assert.Equal(2, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));
        Assert.Equal(2, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM occurrence")));
    }

    [Fact]
    public void Import_RecordsADocumentAlreadyInTheIndexRatherThanFailingOnTheUniqueConstraint()
    {
        var index = SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/one().");

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");
        var report = ScipImporter.Import(db, SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/two()."), "/repo");

        Assert.Equal(1, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));
        Assert.Contains(report.Problems, p => p.Contains("a.ts", StringComparison.Ordinal));
        Assert.True(report.Degraded);
    }

    [Fact]
    public void ImportFile_LeavesTheIndexUntouchedWhenTheFileCannotBeParsed()
    {
        // "A .scip that cannot be parsed degrades the index; it never half-imports in
        // silence." The whole import is one transaction, so a payload that runs out
        // half way through leaves nothing behind at all.
        using var db = Fresh();
        ScipImporter.Import(db, SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/one()."), "/repo");

        var good = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        for (var i = 0; i < 50; i++)
            good.Documents.Add(SingleDocumentIndex($"d{i}.ts", "scip-typescript npm p 1.0 src/`x.ts`/f().").Documents[0]);

        var bytes = good.ToByteArray();
        var truncated = Path.Combine(Path.GetTempPath(), "vela-truncated-" + Guid.NewGuid().ToString("N")[..8] + ".scip");
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);

        try
        {
            Assert.Throws<InvalidDataException>(() => ScipImporter.ImportFile(db, truncated, "/repo"));
            Assert.Equal(1, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));
        }
        finally
        {
            File.Delete(truncated);
        }
    }

    [Fact]
    public void ImportFile_ReadsAnIndexOffDiskWithoutHoldingItWhole()
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                ProjectRoot = new Uri("/repo/").AbsoluteUri,
                ToolInfo = new Scip.ToolInfo { Name = "scip-typescript", Version = "0.4.0" }
            }
        };
        for (var i = 0; i < 5; i++)
            index.Documents.Add(SingleDocumentIndex($"d{i}.ts", $"scip-typescript npm p 1.0 src/`d{i}.ts`/f().").Documents[0]);

        var path = Path.Combine(Path.GetTempPath(), "vela-import-" + Guid.NewGuid().ToString("N")[..8] + ".scip");
        File.WriteAllBytes(path, index.ToByteArray());

        try
        {
            using var db = Fresh();
            var report = ScipImporter.ImportFile(db, path, "/repo");

            Assert.Equal(5, report.Documents);
            Assert.Equal("scip-typescript", report.Tool);
            Assert.Equal(5, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_StoresAnOccurrenceThatCarriesNoSymbolWithoutInventingAName()
    {
        // 4,242 definition-role occurrences of the real index carry no moniker and no
        // SymbolInformation, so a foreign importer sees a definition of something
        // unnameable. The contract: the row is stored, so the totals agree with the
        // index that was read, and it is given no name, because there is none. It can
        // therefore never answer refs, def or impact, and the count is reported so the
        // silence is visible.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        var doc = new Scip.Document { RelativePath = "a.ts", Language = "typescript" };
        doc.Occurrences.Add(new Scip.Occurrence { Range = { 1, 2, 3 } });
        index.Documents.Add(doc);

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo");

        Assert.Equal(1, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM occurrence")));
        Assert.Equal("", Scalar(db, "SELECT symbol FROM occurrence")?.ToString());
        Assert.Equal(1, report.UnnamedOccurrences);

        // An empty name in the full-text index would answer `find` with a blank line.
        Assert.Equal(0, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM symbol_fts")));
    }

    [Fact]
    public void Import_ReadsTheTypedRangeNewProducersAreToldToUse()
    {
        // scip.proto: "New producers SHOULD set `typed_range` and SHOULD NOT set the
        // deprecated `range` field." Reading only the deprecated field would put every
        // occurrence of such an index at line 0, character 0, which looks like a
        // successful import and is uniformly wrong.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        var doc = new Scip.Document { RelativePath = "a.ts", Language = "typescript" };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = "scip-typescript npm p 1.0 src/`a.ts`/one().",
            SingleLineRange = new Scip.SingleLineRange { Line = 12, StartCharacter = 4, EndCharacter = 7 }
        });
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = "scip-typescript npm p 1.0 src/`a.ts`/two().",
            MultiLineRange = new Scip.MultiLineRange { StartLine = 20, StartCharacter = 2, EndLine = 24, EndCharacter = 1 }
        });
        index.Documents.Add(doc);

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");

        Assert.Equal(new[] { 12, 20 }, Column(db, "SELECT start_line FROM occurrence ORDER BY start_line"));
        Assert.Equal(new[] { 4, 2 }, Column(db, "SELECT start_char FROM occurrence ORDER BY start_line"));
    }

    // ================================================================================
    // Helpers
    // ================================================================================

    private static SqliteConnection Fresh()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        return db;
    }

    private static Scip.Document DocumentWithOccurrenceAt(
        string path, string line, Scip.PositionEncoding encoding, int character)
    {
        var doc = new Scip.Document
        {
            RelativePath = path,
            Language = "unknown",
            PositionEncoding = encoding,
            Text = line
        };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = "scip-x . . . a/b#",
            Range = { 0, character, character + 3 }
        });
        return doc;
    }

    private static Scip.Index SingleDocumentIndex(string path, string symbol)
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        var doc = new Scip.Document { RelativePath = path, Language = "typescript" };
        doc.Occurrences.Add(new Scip.Occurrence
        {
            Symbol = symbol,
            SymbolRoles = (int)Scip.SymbolRole.Definition,
            Range = { 0, 0, 3 }
        });
        index.Documents.Add(doc);
        return index;
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static int[] Column(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var values = new List<int>();
        while (reader.Read()) values.Add(reader.GetInt32(0));
        return values.ToArray();
    }
}
