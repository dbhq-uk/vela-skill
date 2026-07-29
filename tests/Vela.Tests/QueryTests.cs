using System.CommandLine;
using Microsoft.Data.Sqlite;
using Vela.Indexing;
using Vela.Query;
using Xunit;

/// <summary>
/// Tests that mutate process-wide state (XDG_CACHE_HOME) belong to this collection so
/// they can never run beside each other. xUnit runs collections in parallel by
/// default, and an environment variable is shared by every test in the process.
/// </summary>
[CollectionDefinition(EnvironmentSensitive.Name, DisableParallelization = true)]
public class EnvironmentSensitive
{
    public const string Name = "environment-sensitive";
}

[Collection(EnvironmentSensitive.Name)]
public class QueryTests
{
    private static SqliteConnection SeededDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document(id, relative_path, language) VALUES
                (1, 'App/Models/Perfume.cs', 'csharp'),
                (2, 'App/Pages/Index.cshtml', 'razor');
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                (1, 'App.Models.Perfume.Status', 1, 10, 4, 12, 5),
                (2, 'App.Models.Perfume.Status', 0, 7, 12, NULL, NULL),
                (1, 'App.Models.Perfume.Name',   1, 20, 4, 22, 5);
            INSERT INTO symbol_fts(symbol) VALUES
                ('App.Models.Perfume.Status'), ('App.Models.Perfume.Name');
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", false, null));
        return db;
    }

    [Fact]
    public void Refs_ReturnsBothCSharpAndRazorOccurrences()
    {
        using var db = SeededDb();
        var hits = RefsQuery.Run(db, "Perfume.Status");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.RelativePath.EndsWith(".cshtml"));
    }

    [Fact]
    public void Def_ReturnsOnlyTheDefinition()
    {
        using var db = SeededDb();
        var hits = DefQuery.Run(db, "Perfume.Status");

        Assert.Single(hits);
        Assert.True(hits[0].IsDefinition);
        Assert.Equal(10, hits[0].Line);
    }

    [Fact]
    public void Outline_ReturnsDefinitionsInOneFile()
    {
        using var db = SeededDb();
        var hits = OutlineQuery.Run(db, "App/Models/Perfume.cs");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.IsDefinition));
    }

    [Fact]
    public void Find_MatchesPartialSymbolNames()
    {
        using var db = SeededDb();
        var symbols = FindQuery.Run(db, "Status");

        Assert.Contains("App.Models.Perfume.Status", symbols);
    }

    /// <summary>
    /// A database whose symbols contain characters that mean something to SQL LIKE
    /// and to FTS5, so a query for one of them cannot quietly match the other.
    /// </summary>
    private static SqliteConnection PunctuationDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document(id, relative_path, language) VALUES
                (1, 'App/Models/Perfume.cs', 'csharp');
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                (1, 'App.Models.Perfume.Foo_Bar', 1, 1, 4, NULL, NULL),
                (1, 'App.Models.Perfume.FooXBar', 1, 2, 4, NULL, NULL);
            INSERT INTO symbol_fts(symbol) VALUES
                ('App.Models.Perfume.Foo_Bar'), ('App.Models.Perfume.FooXBar');
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc123", false, null));
        return db;
    }

    [Fact]
    public void Refs_TreatsUnderscoreAndPercentInThePatternAsLiterals()
    {
        // SQL LIKE reads '_' as "any one character" and '%' as "any run of
        // characters". Underscores are ordinary in .NET identifiers, so without an
        // ESCAPE clause a search for Foo_Bar silently also answers for FooXBar, and
        // the caller has no way to tell the extra hit is not real.
        using var db = PunctuationDb();

        var hits = RefsQuery.Run(db, "Foo_Bar");

        Assert.Single(hits);
        Assert.Equal("App.Models.Perfume.Foo_Bar", hits[0].Symbol);
    }

    [Fact]
    public void Find_TreatsThePatternAsTextRatherThanAsFts5Syntax()
    {
        // FTS5 MATCH has its own query language: '.', '(', '"' and the bare words
        // AND/OR/NOT all mean something in it. A symbol name pasted straight in is
        // therefore either a syntax error or, worse, a different query than asked
        // for. Every one of these is a plausible thing to paste after `vela find`.
        using var db = SeededDb();

        Assert.Contains("App.Models.Perfume.Status", FindQuery.Run(db, "Perfume.Status"));
        Assert.Contains("App.Models.Perfume.Status", FindQuery.Run(db, "Status("));
        Assert.Empty(FindQuery.Run(db, "\"unbalanced"));
        Assert.Empty(FindQuery.Run(db, "NOT"));
        Assert.Empty(FindQuery.Run(db, "..."));
        Assert.Empty(FindQuery.Run(db, "*"));

        // A double quote inside the pattern is escaped into the phrase rather than
        // closing it, so it is searched for as text and the tokens either side still
        // have to appear in that order.
        Assert.Contains("App.Models.Perfume.Status", FindQuery.Run(db, "Perfume\"Status"));
        Assert.Empty(FindQuery.Run(db, "Status\"Perfume"));
    }

    [Fact]
    public void Find_MatchesATrailingPrefix()
    {
        // find is the discovery verb: it is what someone reaches for when they know
        // part of a name. Quoting the pattern as a phrase and stopping there turned
        // every partial name into no answer at all, which for a discovery verb is
        // the same failure as an index that does not contain the code.
        using var db = SeededDb();

        Assert.Contains("App.Models.Perfume.Status", FindQuery.Run(db, "Perfume.Stat"));
        Assert.Contains("App.Models.Perfume.Status", FindQuery.Run(db, "Stat"));
        Assert.Contains("App.Models.Perfume.Name", FindQuery.Run(db, "Nam"));

        // A prefix is still a prefix, not a substring and not a fuzzy match
        // (Constraint 1): "tatus" is inside "Status" and must not match.
        Assert.Empty(FindQuery.Run(db, "tatus"));
    }

    // ---- A match must be a whole dotted segment ---------------------------
    //
    // The suffix match was unanchored and, because SQLite's LIKE is
    // case-insensitive for ASCII, case-blind as well. SQLite agrees:
    // 'App.Models.HttpStatus' LIKE '%Status' is 1, 'Guid' LIKE '%Id' is 1, and
    // 'App.Perfume.Name' LIKE '%name' is 1. So `refs Status` merged Perfume.Status
    // with HttpStatus and OrderStatus, `def Name` answered FirstName and LastName as
    // one result, and `impact Id` attributed callers of Guid. Those are exactly the
    // identifiers - Name, Status, Value, Id, Update - vela exists to disambiguate, so
    // this reintroduced the noise the tool is for.

    /// <summary>
    /// Symbols chosen so that every unanchored or case-blind match has somewhere
    /// wrong to land: HttpStatus ends with Status, Guid ends with Id, FirstName ends
    /// with Name, and Publish carries a parameter list after its name.
    /// </summary>
    private static SqliteConnection SegmentDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document(id, relative_path, language) VALUES
                (1, 'App/Models/Perfume.cs', 'csharp');
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                (1, 'App.Models.Perfume.Status',  1,  1, 4, NULL, NULL),
                (1, 'App.Models.HttpStatus',      1,  2, 4, NULL, NULL),
                (1, 'App.Models.OrderStatus',     1,  3, 4, NULL, NULL),
                (1, 'App.Models.Perfume.Name',    1,  4, 4, NULL, NULL),
                (1, 'App.Models.Person.FirstName',1,  5, 4, NULL, NULL),
                (1, 'App.Models.Person.LastName', 1,  6, 4, NULL, NULL),
                (1, 'System.Guid',                1,  7, 4, NULL, NULL),
                (1, 'App.Models.Perfume.Id',      1,  8, 4, NULL, NULL),
                (1, 'App.Services.PerfumeService.Publish(App.Models.Perfume)', 1, 9, 4, NULL, NULL);
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
        return db;
    }

    [Fact]
    public void Refs_MatchesAWholeDottedSegment_NotAnArbitrarySuffix()
    {
        using var db = SegmentDb();

        var symbols = RefsQuery.Run(db, "Status").Select(h => h.Symbol).ToList();

        Assert.Contains("App.Models.Perfume.Status", symbols);
        Assert.DoesNotContain("App.Models.HttpStatus", symbols);
        Assert.DoesNotContain("App.Models.OrderStatus", symbols);
    }

    [Fact]
    public void Refs_MatchesAQualifiedSuffixAndTheWholeName()
    {
        using var db = SegmentDb();

        Assert.Contains("App.Models.Perfume.Status",
            RefsQuery.Run(db, "Perfume.Status").Select(h => h.Symbol));
        Assert.Contains("App.Models.Perfume.Status",
            RefsQuery.Run(db, "App.Models.Perfume.Status").Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_IsCaseSensitive()
    {
        // SQLite's LIKE is case-insensitive for ASCII, so 'App.Perfume.Name' LIKE
        // '%name' is 1. .NET identifiers are case-sensitive and Name and name are two
        // different members, so a case-blind answer is a wrong answer, not a lenient one.
        using var db = SegmentDb();

        Assert.Empty(RefsQuery.Run(db, "status"));
        Assert.Empty(RefsQuery.Run(db, "perfume.Status"));
        Assert.NotEmpty(RefsQuery.Run(db, "Perfume.Status"));
    }

    [Fact]
    public void Refs_StillMatchesAMethodByItsNameWithoutTheParameterList()
    {
        // Method identities carry a parameter list, so the bare name is not a suffix of
        // the stored symbol. Both the name alone and the fully stored form must match.
        using var db = SegmentDb();

        Assert.Contains("App.Services.PerfumeService.Publish(App.Models.Perfume)",
            RefsQuery.Run(db, "Publish").Select(h => h.Symbol));
        Assert.Contains("App.Services.PerfumeService.Publish(App.Models.Perfume)",
            RefsQuery.Run(db, "PerfumeService.Publish").Select(h => h.Symbol));
        Assert.Contains("App.Services.PerfumeService.Publish(App.Models.Perfume)",
            RefsQuery.Run(db, "App.Services.PerfumeService.Publish(App.Models.Perfume)").Select(h => h.Symbol));
    }

    [Fact]
    public void Def_MatchesAWholeDottedSegment_NotAnArbitrarySuffix()
    {
        using var db = SegmentDb();

        var symbols = DefQuery.Run(db, "Name").Select(h => h.Symbol).ToList();

        Assert.Contains("App.Models.Perfume.Name", symbols);
        Assert.DoesNotContain("App.Models.Person.FirstName", symbols);
        Assert.DoesNotContain("App.Models.Person.LastName", symbols);
    }

    [Fact]
    public void ExplainEmpty_UsesTheSameSegmentMatchingAsTheQueryItself()
    {
        // The "why is this empty" helpers run their own LIKE. If they stayed
        // unanchored, `def Status` on an index holding only HttpStatus would report
        // "occurs in the index but is not defined here", which is a confident claim
        // about a symbol that is not in the index at all.
        using var db = SegmentDb();

        Assert.False(QueryHelper.AnySymbolOccurrence(db, "ttpStatus"));
        Assert.True(QueryHelper.AnySymbolOccurrence(db, "HttpStatus"));
        Assert.False(QueryHelper.AnySymbolOccurrence(db, "httpstatus"));
    }

    [Fact]
    public void Impact_DoesNotAttributeCallersOfADifferentSymbolThatMerelyEndsTheSame()
    {
        // 'Guid' LIKE '%Id' is 1, so `impact Id` reported every caller that touches a
        // Guid as a caller of Perfume.Id.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Services/PerfumeService.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Services.PerfumeService.UsesGuid()', 1, 30, 4, 40, 5),
                    (1, 'System.Guid', 0, 32, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.Empty(ImpactQuery.Run(db, "Id"));
        Assert.NotEmpty(ImpactQuery.Run(db, "Guid"));
    }

    [Fact]
    public void Impact_ReturnsTheDefinitionEnclosingEachReference()
    {
        // The blast radius of a change: which symbols contain a reference to the
        // target, worked out from the stored enclosing ranges.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Services/PerfumeService.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Services.PerfumeService.Publish()', 1, 30, 4, 40, 5),
                    (1, 'App.Services.PerfumeService.Archive()', 1, 50, 4, 60, 5),
                    (1, 'App.Models.Perfume.Status', 0, 32, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var hits = ImpactQuery.Run(db, "Perfume.Status");

        Assert.Single(hits);
        Assert.Equal("App.Services.PerfumeService.Publish()", hits[0].Symbol);
        Assert.Equal(30, hits[0].Line);
    }

    [Fact]
    public void Impact_ReturnsOnlyTheInnermostEnclosingDefinition()
    {
        // Real C# nests: a method sits inside a type, which sits inside a namespace,
        // and the emitter stores an enclosing range for all three. A reference inside
        // the method therefore falls inside three enclosing ranges, but only one of
        // them is the caller. Naming the namespace and the type as callers is noise an
        // agent acts on, and it is the same class of harm as a wrong answer.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Services/PerfumeService.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Services', 1, 0, 0, 80, 1),
                    (1, 'App.Services.PerfumeService', 1, 2, 0, 70, 1),
                    (1, 'App.Services.PerfumeService.Publish()', 1, 30, 4, 40, 5),
                    (1, 'App.Models.Perfume.Status', 0, 32, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var hits = ImpactQuery.Run(db, "Perfume.Status");

        Assert.Single(hits);
        Assert.Equal("App.Services.PerfumeService.Publish()", hits[0].Symbol);
        Assert.Equal(30, hits[0].Line);
    }

    [Fact]
    public void Impact_AttributesAReferenceToTheMemberItActuallySitsIn_NotAMemberSharingItsLine()
    {
        // One line, three definitions and one reference:
        //
        //     class C { void A(){ Helper.Do(); } void B(){} }
        //     0         10       20  |    30        40   |
        //                            27 (the call)       44
        //
        // C spans characters 0..47, A spans 10..34, B spans 35..45, and the call to
        // Helper.Do sits at character 27, inside A. Testing containment by line alone
        // makes all three enclose it, and since only the innermost is returned now,
        // the single answer is B: a named, confident, wrong caller. Naming the wrong
        // method is worse than naming three, because there is nothing in the output
        // that invites a second look.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/OneLiner.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.C',     1, 0,  0, 0, 47),
                    (1, 'App.C.A()', 1, 0, 10, 0, 34),
                    (1, 'App.C.B()', 1, 0, 35, 0, 45),
                    (1, 'App.Helper.Do()', 0, 0, 27, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var hits = ImpactQuery.Run(db, "Helper.Do");

        Assert.Single(hits);
        Assert.Equal("App.C.A()", hits[0].Symbol);
    }

    [Fact]
    public void Impact_DoesNotAttributeAReferenceToADefinitionThatHasAlreadyClosed()
    {
        // The other bound, on one line again:
        //
        //     class C { void A(){} int x = Helper.Do(); }
        //     0         10         21     |             42
        //                                 36 (the call)
        //
        // A closes at character 20, well before the call at 36, so the only
        // definition that really contains the call is C. A line-granular upper bound
        // keeps A a candidate, and A opens later than C, so the innermost rule then
        // picks the method that had already ended.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/OneLiner.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.C',     1, 0,  0, 0, 43),
                    (1, 'App.C.A()', 1, 0, 10, 0, 20),
                    (1, 'App.Helper.Do()', 0, 0, 36, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var hits = ImpactQuery.Run(db, "Helper.Do");

        Assert.Single(hits);
        Assert.Equal("App.C", hits[0].Symbol);
    }

    // ---- Generated documents ---------------------------------------------
    //
    // The Razor generator's output is compiled but never written to disk unless
    // EmitCompilerGeneratedFiles is set, so on a scaffolded Razor app `refs ViewData`
    // answered with fourteen hits of which nine named paths the reader cannot open.
    // That contradicts the whole contract of the tool, which is to report locations you
    // can go to. Suppressing generated documents outright is not available either: for
    // some Razor page members the generated document holds the ONLY definition in the
    // index, so `def` would become unanswerable for them.
    //
    // The resolution: refs and impact exclude generated documents by default and say
    // exactly what they suppressed, def and outline always include them and mark them.

    /// <summary>
    /// One symbol defined only in generated code and referenced from both an openable
    /// file and a generated one.
    /// </summary>
    private static SqliteConnection GeneratedDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document(id, relative_path, language, generated) VALUES
                (1, 'App/Pages/Index.cshtml', 'razor', 0),
                (2, 'App/obj/Debug/net10.0/generated/Pages_Index_cshtml.g.cs', 'csharp', 1);
            INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                (2, 'AspNetCore.Pages_Index.ViewData', 1, 5, 8, 6, 9),
                (2, 'AspNetCore.Pages_Index.ViewData', 0, 9, 8, NULL, NULL),
                (2, 'AspNetCore.Pages_Index.ViewData', 0, 11, 8, NULL, NULL),
                (1, 'AspNetCore.Pages_Index.ViewData', 0, 2, 4, NULL, NULL);
            INSERT INTO symbol_fts(symbol) VALUES ('AspNetCore.Pages_Index.ViewData');
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
        return db;
    }

    [Fact]
    public void Refs_ExcludesGeneratedDocumentsByDefault_AndCountsWhatItSuppressed()
    {
        using var db = GeneratedDb();

        var hits = RefsQuery.Run(db, "ViewData");

        Assert.All(hits, h => Assert.False(h.IsGenerated));
        Assert.All(hits, h => Assert.DoesNotContain(".g.cs", h.RelativePath));
        Assert.Single(hits);

        // Constraint 3: what was left out is named, never silently dropped.
        Assert.Equal(3, RefsQuery.CountInGeneratedCode(db, "ViewData"));
    }

    [Fact]
    public void Refs_WithIncludeGenerated_ReturnsThemAgain()
    {
        using var db = GeneratedDb();

        var hits = RefsQuery.Run(db, "ViewData", includeGenerated: true);

        Assert.Equal(4, hits.Count);
        Assert.Contains(hits, h => h.IsGenerated && h.RelativePath.EndsWith(".g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Impact_ExcludesGeneratedCallersByDefault_AndCountsWhatItSuppressed()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language, generated) VALUES
                    (1, 'App/Services/PerfumeService.cs', 'csharp', 0),
                    (2, 'App/obj/generated/Pages_Index_cshtml.g.cs', 'csharp', 1);
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Services.PerfumeService.Publish()', 1, 30, 4, 40, 5),
                    (1, 'App.Models.Perfume.Status', 0, 32, 12, NULL, NULL),
                    (2, 'AspNetCore.Pages_Index.ExecuteAsync()', 1, 10, 4, 20, 5),
                    (2, 'App.Models.Perfume.Status', 0, 12, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var hits = ImpactQuery.Run(db, "Perfume.Status");

        Assert.Single(hits);
        Assert.Equal("App.Services.PerfumeService.Publish()", hits[0].Symbol);
        Assert.Equal(1, ImpactQuery.CountInGeneratedCode(db, "Perfume.Status"));

        var withGenerated = ImpactQuery.Run(db, "Perfume.Status", includeGenerated: true);
        Assert.Equal(2, withGenerated.Count);
    }

    [Fact]
    public void Def_AlwaysIncludesGeneratedDocuments_AndMarksThem()
    {
        // The generated document holds the only definition of this member anywhere in
        // the index. Suppressing it here would leave `def` with nothing to say about a
        // symbol vela can see perfectly well.
        using var db = GeneratedDb();

        var hits = DefQuery.Run(db, "ViewData");

        var hit = Assert.Single(hits);
        Assert.True(hit.IsGenerated);

        var output = OutputWriter.Render(hits, IndexHealth.Read(db));
        Assert.Contains("(generated)", output, StringComparison.Ordinal);
        Assert.Contains("not written to disk", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Outline_AlwaysIncludesGeneratedDocuments_AndMarksThem()
    {
        using var db = GeneratedDb();

        var hits = OutlineQuery.Run(db, "App/obj/Debug/net10.0/generated/Pages_Index_cshtml.g.cs");

        var hit = Assert.Single(hits);
        Assert.True(hit.IsGenerated);
        Assert.Contains("(generated)", OutputWriter.Render(hits, IndexHealth.Read(db)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefsFromTheCommandLine_DeclaresWhatItSuppressed_AndCanBeAskedForIt()
    {
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteIndexFileWithGeneratedDocument(indexPath);

        var suppressed = await InvokeAsync("refs", "ViewData", "--solution", solution);
        Assert.Equal(0, suppressed.ExitCode);
        Assert.DoesNotContain(".g.cs", suppressed.Output, StringComparison.Ordinal);
        Assert.Contains("3 further", suppressed.Output, StringComparison.Ordinal);
        Assert.Contains("generated code", suppressed.Output, StringComparison.Ordinal);
        Assert.Contains("--include-generated", suppressed.Output, StringComparison.Ordinal);

        var included = await InvokeAsync("refs", "ViewData", "--include-generated", "--solution", solution);
        Assert.Equal(0, included.ExitCode);
        Assert.Contains(".g.cs", included.Output, StringComparison.Ordinal);
        Assert.Contains("(generated)", included.Output, StringComparison.Ordinal);
    }

    // ---- Zero hits have to say why ---------------------------------------
    //
    // Constraint 3 again, in its quieter form. "0 result(s)" reads as an
    // authoritative "there is nothing here", and an agent handed that concludes the
    // symbol is unused, or the file empty, and acts on it. A path typed one
    // directory out, or a symbol that was never indexed, must not be able to
    // produce the same sentence as a genuine absence.

    [Fact]
    public void Outline_OnAPathThatIsNotInTheIndex_SaysTheDocumentIsMissingRatherThanTheSymbols()
    {
        using var db = SeededDb();
        const string path = "Pages/Index.cshtml.cs";   // a real file, one directory out

        var hits = OutlineQuery.Run(db, path);
        Assert.Empty(hits);

        var output = OutputWriter.Render(hits, IndexHealth.Read(db), OutlineQuery.ExplainEmpty(db, path));

        Assert.Contains("No document with the path", output, StringComparison.Ordinal);
        Assert.Contains(path, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Outline_OnAnIndexedPathWithNoDefinitions_SaysTheFileIsIndexedAndEmpty()
    {
        // The other half of the distinction: this file really is indexed and really
        // has nothing in it, and the wording must not blame a missing document.
        using var db = SeededDb();
        const string path = "App/Pages/Index.cshtml";

        var explanation = OutlineQuery.ExplainEmpty(db, path);

        Assert.DoesNotContain("No document with the path", explanation, StringComparison.Ordinal);
        Assert.Contains("is in the index", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_OnAPatternThatMatchesNothing_SaysWhyRatherThanPrintingNothing()
    {
        // The other four verbs explain an empty answer; find printed a bare
        // "0 symbol(s)" and, before that, nothing at all. Under Constraint 3 that is
        // the dangerous shape: find is the discovery verb, so it is what an agent
        // reaches for before concluding a name does not exist in the codebase.
        using var db = SeededDb();
        const string pattern = "Zzzyzx";

        Assert.Empty(FindQuery.Run(db, pattern));

        var explanation = FindQuery.ExplainEmpty(db, pattern);
        Assert.Contains(pattern, explanation, StringComparison.Ordinal);
        Assert.Contains("not evidence", explanation, StringComparison.Ordinal);

        // And it has to reach the caller, not just exist.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, false, null));

        var result = await InvokeAsync("find", pattern, "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0 symbol(s)", result.Output, StringComparison.Ordinal);
        Assert.Contains("not evidence", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_WhenTheIndexHoldsNoSymbolsAtAll_SaysThatRatherThanBlamingThePattern()
    {
        // "nothing matched your pattern" and "there is nothing here to match" are two
        // different answers, and only the first says anything about the code.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));

        var explanation = FindQuery.ExplainEmpty(db, "Status");

        Assert.Contains("no symbols at all", explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refs_OnASymbolThatIsNotInTheIndex_SaysTheSymbolIsNotIndexed()
    {
        using var db = SeededDb();
        const string pattern = "Perfume.Statuz";

        var hits = RefsQuery.Run(db, pattern);
        Assert.Empty(hits);

        var output = OutputWriter.Render(hits, IndexHealth.Read(db), RefsQuery.ExplainEmpty(db, pattern));

        Assert.Contains("No symbol matching", output, StringComparison.Ordinal);
        Assert.Contains(pattern, output, StringComparison.Ordinal);
        Assert.Contains("not evidence", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Def_OnASymbolWhoseDefinitionIsNotIndexed_DoesNotClaimTheSymbolIsUnknown()
    {
        // A symbol that occurs only as a reference: its definition lives in a
        // referenced package. "No such symbol" would be false, so def has to say
        // which of the two absences this is.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Program.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'System.String.IsNullOrEmpty(System.String)', 0, 3, 8, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var explanation = DefQuery.ExplainEmpty(db, "String.IsNullOrEmpty");

        Assert.DoesNotContain("No symbol matching", explanation, StringComparison.Ordinal);
        Assert.Contains("occur in the index", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownPathAndUnknownSymbol_ExplainThemselvesAndStillExitZero()
    {
        // The exit code semantics are already fixed by other tests (0 healthy, 1 no
        // index, 3 degraded), so this is about the message reaching the caller, not
        // about changing the code.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, false, null));

        var outline = await InvokeAsync("outline", "Pages/Index.cshtml.cs", "--solution", solution);
        Assert.Equal(0, outline.ExitCode);
        Assert.Contains("No document with the path", outline.Output, StringComparison.Ordinal);

        var refs = await InvokeAsync("refs", "Perfume.Statuz", "--solution", solution);
        Assert.Equal(0, refs.ExitCode);
        Assert.Contains("No symbol matching", refs.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OnDegradedIndex_SaysSoInTheOutput()
    {
        using var db = SeededDb();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc", true, "App.csproj failed to load"));

        var output = OutputWriter.Render(RefsQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        Assert.Contains("INCOMPLETE", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("App.csproj", output);
    }

    [Fact]
    public void Render_GroupsHitsByFile()
    {
        using var db = SeededDb();
        var output = OutputWriter.Render(RefsQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        Assert.Contains("App/Pages/Index.cshtml", output);
        Assert.Contains("App/Models/Perfume.cs", output);
    }

    [Fact]
    public void Render_ConvertsStoredZeroBasedPositionsToOneBased()
    {
        // Positions are stored exactly as Roslyn produced them, which is zero-based.
        // Every editor, compiler diagnostic and human counts from one, so a
        // rendered position that is off by one sends the reader to the wrong line.
        using var db = SeededDb();
        var output = OutputWriter.Render(DefQuery.Run(db, "Perfume.Status"), IndexHealth.Read(db));

        // Stored (10, 4) is line 11, column 5.
        Assert.Contains("11:5", output);
        Assert.DoesNotContain("10:4", output);
    }

    [Fact]
    public void Render_OnDegradedIndex_WarnsThatAShortResultIsNotProof()
    {
        // Constraint 3: the dangerous reading of a degraded index is the empty
        // result, because "no hits" and "no hits I could see" look identical.
        using var db = SeededDb();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, "abc", true, "App.csproj failed to load"));

        var output = OutputWriter.Render(Array.Empty<Hit>(), IndexHealth.Read(db));

        Assert.Contains("not treat an empty or short result as proof", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- CLI wiring -------------------------------------------------------
    //
    // These exercise the built command tree end to end, because the thing that has
    // to be true of a degraded index is not that the renderer can print a banner,
    // it is that a caller redirecting stdout still learns the answer is incomplete.
    // That signal is the process exit code, and it has to survive parsing,
    // invocation and the return path.

    [Fact]
    public async Task DegradedIndex_ExitsThree_AndHealthyIndexExitsZero()
    {
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);

        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, true, "App.csproj failed to load"));
        var degraded = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(IndexHealth.ExitDegraded, degraded.ExitCode);
        Assert.Contains("INCOMPLETE", degraded.Output, StringComparison.Ordinal);
        Assert.Contains("App/Models/Perfume.cs", degraded.Output, StringComparison.Ordinal);

        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, false, null));
        var healthy = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(0, healthy.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", healthy.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindOnADegradedIndex_AlsoExitsThree()
    {
        // find returns a list of names rather than hits, but an incomplete index
        // makes its answer just as incomplete, so it carries the same signal.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, true, "App.csproj failed to load"));

        var result = await InvokeAsync("find", "Status", "--solution", solution);

        Assert.Equal(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("INCOMPLETE", result.Output, StringComparison.Ordinal);
        Assert.Contains("App.Models.Perfume.Status", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIndexOlderThanTheSourceItDescribes_ReportsDegraded()
    {
        // The index is built once and then answers from a file. Nothing invalidated it,
        // so after any edit every verb kept answering at exit 0, with no banner, at line
        // numbers that had moved. SKILL.md tells the agent to treat a stale index as
        // incomplete, which made the agent's own safety check a no-op: the signal it
        // was told to watch for could never fire.
        //
        // This is the cheap, honest mitigation, not incremental reindex: compare
        // timestamps only, and say so loudly when the tree is newer than the index.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        var source = Path.Combine(repo.Path, "App", "Perfume.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "public class Perfume { }");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);

        var builtAt = DateTime.UtcNow;
        WriteIndexFile(indexPath, new HealthRecord(builtAt, null, false, null));

        // Nothing has changed since the build, so nothing is claimed.
        var fresh = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(0, fresh.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", fresh.Output, StringComparison.Ordinal);

        // Build output is not source and changes constantly; treating it as an edit
        // would make every query degraded forever, which is a signal nobody reads.
        var buildOutput = Path.Combine(repo.Path, "App", "obj", "Debug", "App.dll.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(buildOutput)!);
        File.WriteAllText(buildOutput, "// generated");
        File.SetLastWriteTimeUtc(buildOutput, builtAt.AddMinutes(5));

        var stillFresh = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(0, stillFresh.ExitCode);

        // One edit to a real source file, and the answer can no longer be trusted.
        File.SetLastWriteTimeUtc(source, builtAt.AddMinutes(5));

        var stale = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(IndexHealth.ExitDegraded, stale.ExitCode);
        Assert.Contains("INCOMPLETE", stale.Output, StringComparison.Ordinal);
        Assert.Contains("stale", stale.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("App/Perfume.cs", stale.Output, StringComparison.Ordinal);

        // find answers from the same index, so it carries the same signal.
        var staleFind = await InvokeAsync("find", "Status", "--solution", solution);
        Assert.Equal(IndexHealth.ExitDegraded, staleFind.ExitCode);
        Assert.Contains("stale", staleFind.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingIndex_ExitsNonZeroWithoutKillingTheProcess()
    {
        // The failure path here must return an exit code, never call
        // Environment.Exit: library code that exits the process would take this test
        // host, and anything else hosting vela, down with it. If that regressed,
        // this test would not fail, the whole run would vanish.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var result = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(IndexHealth.ExitDegraded, result.ExitCode);
        Assert.Contains("vela index", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHealthRecord_FoldsInProblemsRecordedByTheEmitter()
    {
        // ScipEmitter records two kinds of problem into the emitted index, and both
        // mean the index is missing code: projects that failed to load, and
        // documents that fall outside the project root and so cannot be represented
        // in SCIP at all. Reading only the loader's failure list would report a
        // complete index while whole files were silently absent from it.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ToolInfo = new Scip.ToolInfo { Name = "vela", Version = "0.0.0" } }
        };
        index.Metadata.ToolInfo.Arguments.Add("outside-project-root: /nuget/RazorLib/Views/Shared.cshtml");

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());

        Assert.True(health.Degraded);
        Assert.Contains("Shared.cshtml", health.Detail);
    }

    [Fact]
    public void BuildHealthRecord_FoldsInAProjectThatProducedNoCompilationAtAll()
    {
        // ScipEmitter used to skip a null compilation with a bare `continue`, recording
        // nothing. Every symbol in that project then failed to resolve, the project's
        // whole contribution vanished from the index, and health said clean.
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ToolInfo = new Scip.ToolInfo { Name = "vela", Version = "0.0.0" } }
        };
        index.Metadata.ToolInfo.Arguments.Add("no-compilation: App produced no compilation");

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());

        Assert.True(health.Degraded);
        Assert.Contains("no-compilation", health.Detail);
    }

    [Fact]
    public void BuildHealthRecord_IsCleanWhenNothingWentWrong()
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata { ToolInfo = new Scip.ToolInfo { Name = "vela", Version = "0.0.0" } }
        };

        var health = Program.BuildHealthRecord(index, Array.Empty<string>());

        Assert.False(health.Degraded);
        Assert.Null(health.Detail);
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        // Output and Error are captured through InvocationConfiguration rather than
        // Console.SetOut, so the capture is scoped to this invocation instead of the
        // whole process. EnableDefaultExceptionHandler is off so that an exception
        // reaches the test as an exception rather than as an exit code.
        using var writer = new StringWriter();
        var configuration = new InvocationConfiguration
        {
            Output = writer,
            Error = writer,
            EnableDefaultExceptionHandler = false
        };

        var exitCode = await Program.BuildRootCommand().Parse(args).InvokeAsync(configuration);
        return (exitCode, writer.ToString());
    }

    private static void WriteIndexFile(string path, HealthRecord health)
    {
        if (File.Exists(path)) File.Delete(path);

        // Pooling off for the same reason vela turns it off: this helper deletes and
        // recreates the file between invocations, and a pooled handle to the deleted
        // file would be served back in place of the new one.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Models/Perfume.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Models.Perfume.Status', 1, 10, 4, 12, 5);
                INSERT INTO symbol_fts(symbol) VALUES ('App.Models.Perfume.Status');
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, health);
    }

    /// <summary>An on-disk index holding one openable document and one generated one.</summary>
    private static void WriteIndexFileWithGeneratedDocument(string path)
    {
        if (File.Exists(path)) File.Delete(path);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language, generated) VALUES
                    (1, 'App/Pages/Index.cshtml', 'razor', 0),
                    (2, 'App/obj/Debug/net10.0/generated/Pages_Index_cshtml.g.cs', 'csharp', 1);
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (2, 'AspNetCore.Pages_Index.ViewData', 1, 5, 8, 6, 9),
                    (2, 'AspNetCore.Pages_Index.ViewData', 0, 9, 8, NULL, NULL),
                    (2, 'AspNetCore.Pages_Index.ViewData', 0, 11, 8, NULL, NULL),
                    (1, 'AspNetCore.Pages_Index.ViewData', 0, 2, 4, NULL, NULL);
                INSERT INTO symbol_fts(symbol) VALUES ('AspNetCore.Pages_Index.ViewData');
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vela-q-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    /// <summary>Points XDG_CACHE_HOME somewhere disposable, and puts it back.</summary>
    private sealed class CacheHome : IDisposable
    {
        private readonly string? _previous;

        public CacheHome(string path)
        {
            _previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", path);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _previous);
    }
}
