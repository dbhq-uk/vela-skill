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
                "src.composables.useApi.formatErrorMessage")]
    [InlineData("scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/formatErrorMessage().(error)",
                "src.composables.useApi.formatErrorMessage.error")]
    [InlineData("scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/useAsyncData().[T]",
                "src.composables.useApi.useAsyncData.T")]
    [InlineData("scip-typescript npm @vue/reactivity 3.5.32 dist/`reactivity.d.ts`/Ref#",
                "dist.reactivity.Ref")]
    [InlineData("scip-typescript npm typescript 5.9.3 lib/`lib.es5.d.ts`/Error#message.",
                "lib.lib_es5.Error.message")]
    [InlineData("scip-typescript npm vue-router 5.0.4 dist/`index-BzEKChPW.d.ts`/`'vue'`/",
                "dist.index-BzEKChPW.'vue'")]
    [InlineData("scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/",
                "src.composables.useApi")]
    [InlineData("scip-typescript npm vue 3.5.32 dist/`vue.d.mts`/defineComponent().",
                "dist.vue.defineComponent")]

    // A module descriptor's extension goes, and nothing else about a descriptor name
    // does. Every extension in this list is one an indexer in the ecosystem really
    // writes into a namespace descriptor.
    [InlineData("scip-x . . . `a.ts`/`b.tsx`/`c.js`/`d.jsx`/`e.mjs`/`f.cjs`/", "a.b.c.d.e.f")]
    [InlineData("scip-x . . . `a.py`/`b.go`/`c.rb`/`d.java`/`e.kt`/`f.scala`/", "a.b.c.d.e.f")]
    [InlineData("scip-x . . . `a.rs`/`b.php`/`c.c`/`d.cc`/`e.cpp`/`f.h`/`g.hpp`/", "a.b.c.d.e.f.g")]

    // Only a namespace descriptor names a module, so only a namespace descriptor loses
    // an extension. A type, a term and a method are named after code, and a name that
    // happens to end in one of those extensions is that name.
    [InlineData("scip-x . . . `mod.ts`/`Wrapper.ts`#", "mod.Wrapper_ts")]
    [InlineData("scip-x . . . `mod.ts`/`value.js`.", "mod.value_js")]
    [InlineData("scip-x . . . `mod.ts`/`run.py`().", "mod.run_py")]

    // An extension is only an extension when a name is left after it goes, and a
    // namespace whose name is an extension without the dot is just that name.
    [InlineData("scip-x . . . `.ts`/A#", "_ts.A")]
    [InlineData("scip-x . . . ts/A#", "ts.A")]

    // A dot the extension rule does not account for still cannot open a segment: a
    // module whose name really carries one, and TypeScript's own multi-part library
    // names, which come straight out of a real index.
    [InlineData("scip-x . . . src/`a.b.ts`/props.", "src.a_b.props")]
    [InlineData("scip-typescript npm typescript 5.9.3 lib/`lib.es2015.symbol.wellknown.d.ts`/Symbol#",
                "lib.lib_es2015_symbol_wellknown.Symbol")]

    // The component-file extensions. A single-file component is a module like any other
    // and its name is the name a person types: `refs Card` has to reach the module and
    // not only the symbols inside it, and these read Card_vue and Card_svelte until the
    // extensions were recognised. ScentVerdict.Mobile, the app vela was measured on, is
    // a Vue app, so this was not hypothetical.
    [InlineData("scip-x . . . src/`Card.vue`/props.", "src.Card.props")]
    [InlineData("scip-x . . . src/`Card.svelte`/props.", "src.Card.props")]
    [InlineData("scip-x . . . src/`Page.astro`/props.", "src.Page.props")]
    [InlineData("scip-x . . . docs/`Guide.mdx`/heading.", "docs.Guide.heading")]

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

    [Fact]
    public void Import_CountsTheDocumentsThatDeclareNoPositionEncodingRatherThanAssumingOneInSilence()
    {
        // scip.proto, of UnspecifiedPositionEncoding = 0: "Default value. This value
        // should not be used by new SCIP indexers so that a consumer can process the
        // SCIP index without ambiguity." A real index uses it anyway - scip-typescript
        // 0.4.0 leaves the field unset on every document it writes - and NeedsConversion
        // handles only UTF-8 and UTF-32, so 0 fell through as though the indexer had
        // declared UTF-16 and was counted nowhere.
        //
        // vela still reads it as UTF-16, because that is what every row in the index
        // already means and there is nothing else to read it as: the unit is decided by
        // the indexer's implementation language, which the file does not state. What it
        // must not do is call that a fact. It is right for scip-typescript and silently
        // wrong for an indexer written in Go, Rust, C++ or Python that omits the field,
        // so the number is counted and reported (Constraint 3).
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };

        // Declared nothing at all, which is what a real scip-typescript index looks like.
        index.Documents.Add(new Scip.Document { RelativePath = "silent.ts", Language = "typescript" });

        // Declared UTF-16, so it is not ambiguous and is not counted.
        index.Documents.Add(new Scip.Document
        {
            RelativePath = "declared.ts",
            Language = "typescript",
            PositionEncoding = Scip.PositionEncoding.Utf16CodeUnitOffsetFromLineStart
        });

        // Declared UTF-8, which is a different fact reported by a different number.
        index.Documents.Add(new Scip.Document
        {
            RelativePath = "declared.go",
            Language = "go",
            PositionEncoding = Scip.PositionEncoding.Utf8CodeUnitOffsetFromLineStart
        });

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo");

        Assert.Equal(3, report.Documents);
        Assert.Equal(1, report.UnspecifiedEncodingDocuments);

        // The declared encoding is stored exactly as it arrived, 0 included, so what the
        // index said and what vela assumed are both recoverable.
        // Ordered by path: declared.go, declared.ts, silent.ts.
        Assert.Equal(
            new[] { 1, 2, 0 },
            Column(db, "SELECT position_encoding FROM document ORDER BY relative_path"));
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

        // And they are namespaced the way every other imported name is shaped: dotted
        // segments, each one askable. The document they belong to is what makes them two
        // names, and 'local 1' - a word, a space and a number - is not a name vela's
        // matching rule can reach at all.
        Assert.Equal(
            new[] { "a.local1", "b.local1" },
            Strings(db, "SELECT symbol FROM occurrence ORDER BY symbol"));

        // The moniker itself is left exactly as the index wrote it. It is theirs, it is
        // what an export has to put back, and the spec already forbids reaching it from
        // outside the document, so it is the display name that carries the namespacing.
        Assert.Equal(1, Convert.ToInt32(Scalar(db, "SELECT COUNT(DISTINCT scip_symbol) FROM occurrence")));
    }

    [Fact]
    public void Import_NamesALocalWithoutAnExtensionOrAnythingElseThatCannotBeASegment()
    {
        // The document a local is namespaced by is a path, and a path is not dotted the
        // way a name is: '/' separates its components, its last component carries a file
        // extension, and a directory can have a dot in its own name. Turned into
        // segments naively, every local in the tree would end up under a segment called
        // 'ts' and a segment called 'Mobile', which are catch-alls answering for
        // hundreds of unrelated symbols. One path component is one segment, by the same
        // two rules a descriptor gets.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };

        var doc = new Scip.Document { RelativePath = "src/ScentVerdict.Mobile/src/useApi.ts" };
        doc.Occurrences.Add(new Scip.Occurrence { Symbol = "local 2", Range = { 0, 4, 9 } });
        index.Documents.Add(doc);

        using var db = Fresh();
        ScipImporter.Import(db, index, "/repo");

        Assert.Equal(
            "src.ScentVerdict_Mobile.src.useApi.local2",
            Scalar(db, "SELECT symbol FROM occurrence")?.ToString());
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

        // The document it lives in is still what makes it document-scoped, and it stays
        // there even though the enclosing symbol repeats most of it: an indexer that
        // fills in display_name and leaves enclosing_symbol empty would otherwise store
        // a bare 'count', which is one name for every count in the repository. Matching
        // is on a trailing run of segments, so the prefix costs nothing: `refs count`,
        // `refs First.count` and the whole name all reach this row.
        Assert.Equal(
            "App.Counter.App.Counter.First.count",
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

    [Theory]
    // scip.proto calls project_root a "URI-encoded absolute path to the root directory
    // of this index" and does NOT mark it required, unlike Document.relative_path which
    // is "(Required)". So an index can really arrive with it empty or relative, and the
    // rebase used to do Path.GetFullPath(Path.Combine("", relativePath)), which resolves
    // against the PROCESS WORKING DIRECTORY. The same .scip file imported from two
    // different directories then produced different paths, or a clean import from one
    // directory and a degraded one from the other, which is not a deterministic tool
    // (Constraint 1). It is refused instead, by name, because there is nothing to guess
    // from: the paths in the file are relative to a directory the file does not name.
    [InlineData("")]
    [InlineData("src/Mobile")]
    [InlineData("./src")]
    [InlineData("../sibling")]
    [InlineData("https://example.com/repo")]
    public void Import_RefusesAProjectRootThatIsNotAnAbsolutePathRatherThanResolvingItAgainstTheProcessDirectory(
        string projectRoot)
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = projectRoot }
        };
        index.Documents.Add(new Scip.Document { RelativePath = "src/api.ts", Language = "typescript" });

        using var db = Fresh();

        var ex = Assert.Throws<InvalidDataException>(() => ScipImporter.Import(db, index, "/repo"));
        Assert.Contains("project_root", ex.Message, StringComparison.Ordinal);

        // Refused, so nothing at all was written: the index is exactly as it was.
        Assert.Equal(0, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));
        Assert.Equal(0, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM occurrence")));
    }

    [Fact]
    public void Import_AcceptsABareAbsolutePathAsAProjectRootAsWellAsAFileUri()
    {
        // Every indexer in the ecosystem writes a file: URI, which is what "URI-encoded"
        // asks for, but a bare absolute path names the same directory unambiguously and
        // there is no reason to refuse it. What is refused is only what cannot be
        // resolved without inventing a directory.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = "/repo/src/Mobile" }
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

    [Fact]
    public void Import_RecordsEveryPathItCannotRebaseWhereItCanBeReadBackAndNotOnlyInTheBanner()
    {
        // These paths used to land only in ImportReport.Problems, which becomes the
        // health record's detail, which Program.Summarise truncates at ten entries: past
        // the tenth they became "(+N more)" and no longer existed anywhere. The brief
        // said record, and a truncated string is not a record. vela already has a table
        // for exactly this fact - the files an index deliberately does not hold, named
        // rather than counted - and `vela index --stats` already prints it.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/elsewhere/").AbsoluteUri }
        };

        // Twelve, so the truncation that lost them is in play.
        for (var i = 0; i < 12; i++)
            index.Documents.Add(new Scip.Document { RelativePath = $"Shared/Thing{i}.go", Language = "go" });

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo");

        // Still a gap in the index, still degraded, still named in the report: this adds
        // a record, it does not soften the verdict.
        Assert.True(report.Degraded);
        Assert.Equal(12, report.Problems.Count);
        Assert.Equal(0, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));

        var external = ExternalDocuments.Read(db);
        Assert.Equal(12, external.Count);
        Assert.Contains("/elsewhere/Shared/Thing11.go", external);

        // And the same .scip imported again does not record them twice. The health
        // contribution is replaced by source; this list would otherwise grow without
        // bound in exactly the way that record did.
        ScipImporter.Import(db, index, "/repo");
        Assert.Equal(12, ExternalDocuments.Read(db).Count);
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

    // ================================================================================
    // Two symbols arriving at one display name
    // ================================================================================

    [Fact]
    public void Import_ReportsADisplayNameReachedFromMoreThanOneDistinctScipSymbol()
    {
        // The derivation can make two symbols one name two ways: a module descriptor
        // loses its file extension, so a folder `utils/` and a file `utils.ts` beside it
        // both become `utils`, and any dot still left in a descriptor becomes an
        // underscore, so a module `a.b.ts` and a module `a_b.ts` both become `a_b`.
        //
        // Neither was visible. The defence for the second was that over-answering "is
        // visible in the ambiguity block", but Ambiguity groups by the display name, so
        // the two symbols are presented as ONE, the impact count silently overstates and
        // nothing anywhere says a name is doing double duty. The importer holds both
        // sides at the moment it derives the name, so it is the only place that can tell.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };

        var doc = new Scip.Document { RelativePath = "a.ts", Language = "typescript" };
        foreach (var moniker in new[]
                 {
                     // A barrel file beside the folder it re-exports: src/utils.ts and
                     // src/utils/one.ts. This is an ordinary TypeScript layout.
                     "scip-x . . . src/`utils.ts`/one().",
                     "scip-x . . . src/utils/one().",

                     // A dot in a module name that no extension rule accounts for.
                     "scip-x . . . `a.b.ts`/two().",
                     "scip-x . . . `a_b.ts`/two()."
                 })
        {
            doc.Occurrences.Add(new Scip.Occurrence { Symbol = moniker, Range = { 0, 0, 3 } });
        }
        index.Documents.Add(doc);

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo");

        Assert.Equal(new[] { "a_b.two", "src.utils.one" }, report.CollidingDisplayNames);

        // Nothing is lost, which is why this is a report and not a refusal: the moniker
        // each occurrence arrived with is still in the row beside the display name, so
        // which is which is there to be read.
        Assert.Equal(4, Convert.ToInt32(Scalar(db, "SELECT COUNT(DISTINCT scip_symbol) FROM occurrence")));
        Assert.Equal(2, Convert.ToInt32(Scalar(db, "SELECT COUNT(DISTINCT symbol) FROM occurrence")));
    }

    [Fact]
    public void Import_DoesNotReportTheMergesItDocumentsAsCollisions()
    {
        // The rule has to stay quiet on the merges the derivation documents, or it is the
        // crying-wolf failure again: a method loses its signature, so two overloads share
        // a name, and a .NET type loses its generic arity, so ILogger and ILogger`1 share
        // one - both deliberate, both stated, and both present in any real .NET index.
        // What is reported is two DIFFERENT descriptor names becoming one segment, which
        // is the thing no rule intended.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };

        var doc = new Scip.Document { RelativePath = "a.cs", Language = "csharp" };
        foreach (var moniker in new[]
                 {
                     "scip-dotnet nuget App 1.0.0.0 App/Svc#Publish().",
                     "scip-dotnet nuget App 1.0.0.0 App/Svc#Publish(+1).",
                     "scip-dotnet nuget L 1.0.0.0 Microsoft/Extensions/Logging/ILogger#",
                     "scip-dotnet nuget L 1.0.0.0 Microsoft/Extensions/Logging/`ILogger``1`#",

                     // The package is dropped from the name too, so the same code indexed
                     // under two package names is one symbol and not a collision.
                     "scip-dotnet nuget App 2.0.0.0 App/Svc#Publish()."
                 })
        {
            doc.Occurrences.Add(new Scip.Occurrence { Symbol = moniker, Range = { 0, 0, 3 } });
        }
        index.Documents.Add(doc);

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo");

        Assert.Empty(report.CollidingDisplayNames);
    }

    // ================================================================================
    // Importing the same .scip again: --replace
    // ================================================================================

    [Fact]
    public void Import_WithReplace_ReplacesTheDocumentsTheIncomingIndexCarriesAndReportsHowMany()
    {
        // Without this the verb is single-shot: every document of a second import
        // collides on the UNIQUE relative_path, nothing is written and the index is
        // degraded, which is honest and useless. Re-running the indexer after the code
        // changed is the only workflow anybody has.
        using var db = Fresh();
        ScipImporter.Import(db, SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/one()."), "/repo");

        var report = ScipImporter.Import(
            db, SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/two()."), "/repo", replace: true);

        Assert.False(report.Degraded, string.Join("; ", report.Problems));
        Assert.Equal(1, report.ReplacedDocuments);
        Assert.Equal(1, report.ReplacedOccurrences);
        Assert.Equal(1, report.ReplacementOccurrences);

        // One document, holding what the new .scip said and not what the old one did.
        Assert.Equal(1, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM document")));
        Assert.Equal(new[] { "src.a.two" }, Strings(db, "SELECT symbol FROM occurrence"));

        // And the full-text row for the name that went away went with it, exactly once,
        // leaving the new name there exactly once. An orphaned FTS row answers `vela
        // find` with a name no occurrence in the index carries.
        Assert.Equal(new[] { "src.a.two" }, Strings(db, "SELECT symbol FROM symbol_fts ORDER BY symbol"));
    }

    [Fact]
    public void Import_WithReplace_KeepsAnFtsNameAnotherDocumentStillUsesAndNeverWritesOneTwice()
    {
        // The orphan sweep has to be exact in both directions. A name the replaced
        // document shared with a document nobody touched must survive, and it must not
        // gain a second row either, or `vela find` starts printing it twice and every
        // re-import prints it once more.
        var shared = "scip-typescript npm p 1.0 src/`x.ts`/shared().";

        using var db = Fresh();
        ScipImporter.Import(db, SingleDocumentIndex("a.ts", shared), "/repo");
        ScipImporter.Import(db, SingleDocumentIndex("b.ts", shared), "/repo");

        Assert.Equal(new[] { "src.x.shared" }, Strings(db, "SELECT symbol FROM symbol_fts"));

        ScipImporter.Import(db, SingleDocumentIndex("a.ts", shared), "/repo", replace: true);

        Assert.Equal(2, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM occurrence")));
        Assert.Equal(new[] { "src.x.shared" }, Strings(db, "SELECT symbol FROM symbol_fts"));
    }

    [Fact]
    public void Import_WithReplace_SaysWhenWhatItWroteIsSmallerThanWhatItReplaced()
    {
        // An index that shrinks is a fact worth reporting, not an error: it is what
        // re-running the indexer over code that lost a symbol looks like. It is also
        // what a broken indexer run looks like, and the two are indistinguishable from
        // here, so the numbers are printed and neither is assumed.
        var big = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        var wide = new Scip.Document { RelativePath = "a.ts", Language = "typescript" };
        foreach (var name in new[] { "one", "two", "three" })
        {
            wide.Occurrences.Add(new Scip.Occurrence
            {
                Symbol = $"scip-typescript npm p 1.0 src/`a.ts`/{name}().",
                SymbolRoles = (int)Scip.SymbolRole.Definition,
                Range = { 0, 0, 3 }
            });
        }
        big.Documents.Add(wide);

        using var db = Fresh();
        ScipImporter.Import(db, big, "/repo");

        var report = ScipImporter.Import(
            db, SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/one()."), "/repo", replace: true);

        Assert.Equal(1, report.ReplacedDocuments);
        Assert.Equal(3, report.ReplacedOccurrences);
        Assert.Equal(1, report.ReplacementOccurrences);

        // Nothing is left behind that the replacement no longer accounts for.
        Assert.Equal(1, Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM occurrence")));
        Assert.Equal(new[] { "src.a.one" }, Strings(db, "SELECT symbol FROM symbol_fts ORDER BY symbol"));
    }

    [Fact]
    public void Import_WithReplace_StillReportsTwoDocumentsOfOneIndexClaimingOnePath()
    {
        // --replace must not become a way to lose rows in silence. scip.proto calls
        // relative_path a "Unique path to the text document", so one .scip naming one
        // file twice is a broken .scip: the file cannot be both, and replacing the first
        // with the second would keep whichever came last and say nothing.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ProjectRoot = new Uri("/repo/").AbsoluteUri }
        };
        index.Documents.Add(SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/one().").Documents[0]);
        index.Documents.Add(SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/two().").Documents[0]);

        using var db = Fresh();
        var report = ScipImporter.Import(db, index, "/repo", replace: true);

        Assert.Equal(1, report.Documents);
        Assert.Equal(0, report.ReplacedDocuments);
        Assert.True(report.Degraded);
        Assert.Contains(report.Problems, p => p.Contains("a.ts", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_WithoutReplace_StillRefusesToOverwriteADocumentAlreadyInTheIndex()
    {
        // The default has not moved. A silent overwrite of a document somebody else's
        // index also claims is exactly the failure ImportReport.Problems exists to make
        // visible, so replacing is something the caller asks for by name.
        using var db = Fresh();
        ScipImporter.Import(db, SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/one()."), "/repo");
        var report = ScipImporter.Import(
            db, SingleDocumentIndex("a.ts", "scip-typescript npm p 1.0 src/`a.ts`/two()."), "/repo");

        Assert.Equal(0, report.ReplacedDocuments);
        Assert.True(report.Degraded);
        Assert.Equal(new[] { "src.a.one" }, Strings(db, "SELECT symbol FROM occurrence"));
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

    private static string[] Strings(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
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
