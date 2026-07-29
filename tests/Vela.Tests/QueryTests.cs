using System.CommandLine;
using System.Text.RegularExpressions;
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

    // ---- A parameter list is not part of the name, and what follows it is --
    //
    // The parameter list was stripped at the FIRST '(', which threw away every
    // segment after the closing ')'. A constructor parameter's stored identity is
    // exactly that shape:
    //
    //   App.Services.PerfumeService.PerfumeService(ILogger<...>, IImageService).logger
    //
    // Cut at the first '(' that reads App.Services.PerfumeService.PerfumeService,
    // whose last segment is PerfumeService, so `refs PerfumeService` answered with
    // occurrences of the constructor's parameters as though they were the type. On
    // the real solution 6 of its 13 results were parameter variables, and the same
    // thing happens to every method: `refs Get` answered 9,493 where 362 are real,
    // the other 9,131 being locals and parameters declared inside some Get(...).
    //
    // Those are precisely the false hits vela exists to remove, and the shape fires
    // on every dependency-injected service class in every codebase.
    //
    // So the parameter list ends at the last ')' that a name follows, and whatever
    // follows it is part of the name: a parameter is reachable by its own name, and
    // is not reachable by the name of the type or method it is declared in.

    private const string Constructor =
        "App.Services.PerfumeService.PerfumeService(Microsoft.Extensions.Logging.ILogger"
      + "<App.Services.PerfumeService>, App.Services.IImageService)";

    private const string Publish = "App.Services.PerfumeService.Publish(App.Models.Perfume)";

    private const string Report =
        "App.Reporting.Reporter.Report(Microsoft.Extensions.Logging.ILogger"
      + "<App.Models.Perfume<App.Models.Note>>, App.Models.Baz)";

    /// <summary>
    /// The real shapes, in miniature: a type, its constructor, that constructor's
    /// parameters, a method, a local inside it, and a method whose parameter list
    /// nests generic arguments two deep.
    /// </summary>
    private static SqliteConnection ParameterListDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        var symbols = new[]
        {
            "App.Services.PerfumeService",
            Constructor,
            Constructor + ".logger",
            Constructor + ".imageService",
            Publish,
            Publish + ".draft",
            Report,
            Report + ".state"
        };

        using (var document = db.CreateCommand())
        {
            document.CommandText =
                "INSERT INTO document(id, relative_path, language) VALUES (1, 'App/Services/PerfumeService.cs', 'csharp')";
            document.ExecuteNonQuery();
        }

        for (var i = 0; i < symbols.Length; i++)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char) "
              + "VALUES (1, $s, 1, $l, 4)";
            cmd.Parameters.AddWithValue("$s", symbols[i]);
            cmd.Parameters.AddWithValue("$l", i);
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
        return db;
    }

    [Fact]
    public void Refs_MatchesTheTypeAndItsConstructor_ButNotTheConstructorsParameters()
    {
        // The motivating case. A parameter of PerfumeService's constructor is a
        // variable; it is not the type, and an answer that counts it as one is the
        // noise this tool exists to remove.
        using var db = ParameterListDb();

        var symbols = RefsQuery.Run(db, "PerfumeService").Select(h => h.Symbol).ToList();

        Assert.Contains("App.Services.PerfumeService", symbols);
        Assert.Contains(Constructor, symbols);
        Assert.DoesNotContain(Constructor + ".logger", symbols);
        Assert.DoesNotContain(Constructor + ".imageService", symbols);
        Assert.DoesNotContain(Publish + ".draft", symbols);
    }

    [Fact]
    public void Refs_MatchesAMethodWithAndWithoutItsParameterList()
    {
        using var db = ParameterListDb();

        Assert.Contains(Publish, RefsQuery.Run(db, "Publish").Select(h => h.Symbol));
        Assert.Contains(Publish, RefsQuery.Run(db, "PerfumeService.Publish").Select(h => h.Symbol));
        Assert.Contains(Publish, RefsQuery.Run(db, Publish).Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_MatchesALocalOrParameterByItsOwnName_AndByTheNameItIsDeclaredIn()
    {
        // The segments after the parameter list are the ones a reader can see in the
        // source, so they have to be the ones that match. Lengthening the pattern
        // across the stripped parameter list has to keep working too: the answer's
        // own advice is to lengthen a name until one symbol is left.
        using var db = ParameterListDb();

        Assert.Equal(new[] { Constructor + ".logger" },
            RefsQuery.Run(db, "logger").Select(h => h.Symbol));
        Assert.Equal(new[] { Publish + ".draft" },
            RefsQuery.Run(db, "draft").Select(h => h.Symbol));
        Assert.Equal(new[] { Constructor + ".logger" },
            RefsQuery.Run(db, "PerfumeService.logger").Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_HandlesAParameterListThatNestsGenericArguments()
    {
        // Report(ILogger<Perfume<Note>>, Baz) carries no nested parentheses but plenty
        // of nested punctuation, and its local is stored after the closing ')'.
        using var db = ParameterListDb();

        Assert.Contains(Report, RefsQuery.Run(db, "Report").Select(h => h.Symbol));
        Assert.Contains(Report, RefsQuery.Run(db, "Reporter.Report").Select(h => h.Symbol));
        Assert.Equal(new[] { Report + ".state" }, RefsQuery.Run(db, "state").Select(h => h.Symbol));

        // The method's own containing type does not reach into its body, and a type
        // named inside the parameter list is not the method's name.
        Assert.DoesNotContain(Report + ".state", RefsQuery.Run(db, "Reporter").Select(h => h.Symbol));
        Assert.Empty(RefsQuery.Run(db, "Note"));
        Assert.Empty(RefsQuery.Run(db, "Baz"));
    }

    [Fact]
    public void Refs_AcrossAParameterList_IsStillCaseSensitiveAndStillHasNoWildcards()
    {
        using var db = ParameterListDb();

        Assert.Empty(RefsQuery.Run(db, "perfumeService"));
        Assert.Empty(RefsQuery.Run(db, "LOGGER"));

        // '_' would be "any one character" and '%' "any run of characters" if a
        // pattern language had crept back in with the parameter-list handling.
        Assert.Empty(RefsQuery.Run(db, "logge_"));
        Assert.Empty(RefsQuery.Run(db, "%"));
        Assert.Empty(RefsQuery.Run(db, "%logger"));
    }

    [Fact]
    public void Impact_DoesNotAttributeCallersOfALocalToTheTypeItIsDeclaredIn()
    {
        // The same defect, where it costs most: a blast radius sized from a count that
        // included every parameter of every constructor of the type asked about.
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Services/PerfumeService.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Services.PerfumeService.Publish(App.Models.Perfume)', 1, 30, 4, 40, 5),
                    (1, '{Constructor}.logger', 0, 32, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.Empty(ImpactQuery.Run(db, "PerfumeService"));
        Assert.NotEmpty(ImpactQuery.Run(db, "logger"));
    }

    // ---- A generic type argument list is not part of the name either ----------
    //
    // The parameter list was stripped; the type argument list was not. So every
    // constructed generic carried its arguments in its last dotted segment and no
    // bare name could reach it:
    //
    //   Microsoft.Extensions.Logging.ILogger<ScentVerdict.Web.Pages.IndexModel>
    //   ScentVerdict.Ai.Auditing.AuditRunner.RunWithAuditAsync<(int, int)>(System.String)
    //
    // On the real solution `refs ILogger` found 2 occurrences of 271, and
    // `refs RunWithAuditAsync` matched none of its 33 instantiations. 13,366 of the
    // index's 135,321 distinct symbols carry a '<' before any parameter list, so this
    // is the same silent under-count as the parameter list, over a tenth of the index:
    // the tool reports a widely used symbol as barely used, which is exactly the
    // conclusion that makes an agent delete something.
    //
    // A type argument list can contain anything a type can: dots, commas, nested
    // argument lists, and a tuple's parentheses. So it is removed by matching angle
    // brackets rather than by cutting at the first one, and a name whose brackets do
    // not balance, which is what 'operator <(A, B)' is, is left exactly as it is.

    private const string LoggerOfService = "App.Logging.ILogger<App.Services.PerfumeService>";

    private const string LoggerOfList =
        "App.Logging.ILogger<App.Collections.List<App.Models.Note>>";

    private const string RunWithAudit =
        "App.Auditing.AuditRunner.RunWithAuditAsync<System.String>(System.String)";

    private const string RunWithAuditTuple =
        "App.Auditing.AuditRunner.RunWithAuditAsync<(System.Int32, System.Int32)>(System.String)";

    private const string HandlerInvoke =
        "App.Events.Handler<System.Func<System.Int32, System.String>>.Invoke";

    private const string DictionaryOfLists =
        "System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>";

    private const string LessThan = "App.Models.Money.operator <(App.Models.Money, App.Models.Note)";

    private const string GreaterThan = "App.Models.Money.operator >(App.Models.Money, App.Models.Note)";

    /// <summary>
    /// The generic shapes, in miniature: an open generic type and two constructions of
    /// it, one of them nested; a generic method and the same method with a tuple type
    /// argument, which puts a comma and a pair of parentheses inside the angle
    /// brackets; a local declared inside one of them; a member reached across a type
    /// argument list that itself contains dots and parentheses; a type with two type
    /// arguments; and the two comparison operators, whose stored names contain an angle
    /// bracket that opens nothing.
    /// </summary>
    private static SqliteConnection TypeArgumentDb()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        var symbols = new[]
        {
            "App.Logging.ILogger",
            LoggerOfService,
            LoggerOfList,
            RunWithAudit,
            RunWithAudit + ".attempt",
            RunWithAuditTuple,
            HandlerInvoke,
            DictionaryOfLists,
            "App.Models.Money",
            LessThan,
            GreaterThan
        };

        using (var document = db.CreateCommand())
        {
            document.CommandText =
                "INSERT INTO document(id, relative_path, language) VALUES (1, 'App/Logging/Logger.cs', 'csharp')";
            document.ExecuteNonQuery();
        }

        for (var i = 0; i < symbols.Length; i++)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char) "
              + "VALUES (1, $s, 1, $l, 4)";
            cmd.Parameters.AddWithValue("$s", symbols[i]);
            cmd.Parameters.AddWithValue("$l", i);
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
        return db;
    }

    [Fact]
    public void Refs_MatchesAGenericTypeWhateverItsTypeArguments()
    {
        // The motivating case. ILogger<T> is one type, and an answer that reaches only
        // the occurrences written without type arguments reports a type used everywhere
        // as used almost nowhere.
        using var db = TypeArgumentDb();

        var symbols = RefsQuery.Run(db, "ILogger").Select(h => h.Symbol).ToList();

        Assert.Contains("App.Logging.ILogger", symbols);
        Assert.Contains(LoggerOfService, symbols);
        Assert.Contains(LoggerOfList, symbols);

        // Lengthening the name across the type argument list has to keep working, since
        // that is what the ambiguity block tells the reader to do.
        Assert.Equal(3, RefsQuery.Run(db, "Logging.ILogger").Count);
        Assert.Equal(3, RefsQuery.Run(db, "App.Logging.ILogger").Count);
    }

    [Fact]
    public void Refs_MatchesAGenericMethodByItsNameAndByItsParameterList()
    {
        using var db = TypeArgumentDb();

        var byName = RefsQuery.Run(db, "RunWithAuditAsync").Select(h => h.Symbol).ToList();
        Assert.Contains(RunWithAudit, byName);
        Assert.Contains(RunWithAuditTuple, byName);

        // A method's parameter list is part of how it is written down, so a pattern
        // carrying it must still reach the method whatever its type arguments are.
        var bySignature = RefsQuery.Run(db, "RunWithAuditAsync(System.String)").Select(h => h.Symbol).ToList();
        Assert.Contains(RunWithAudit, bySignature);
        Assert.Contains(RunWithAuditTuple, bySignature);

        Assert.Contains(RunWithAudit,
            RefsQuery.Run(db, "AuditRunner.RunWithAuditAsync").Select(h => h.Symbol));
        Assert.Contains(RunWithAudit, RefsQuery.Run(db, RunWithAudit).Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_MatchesASegmentReachedAcrossATypeArgumentList()
    {
        // Handler<Func<int, string>>.Invoke puts dots, a comma and two closing angle
        // brackets between the type's name and the member's, so a rule that cut at the
        // first '<' or paired the wrong brackets would lose the member entirely.
        using var db = TypeArgumentDb();

        Assert.Equal(new[] { HandlerInvoke }, RefsQuery.Run(db, "Invoke").Select(h => h.Symbol));
        Assert.Equal(new[] { HandlerInvoke }, RefsQuery.Run(db, "Handler.Invoke").Select(h => h.Symbol));
        Assert.Equal(new[] { HandlerInvoke }, RefsQuery.Run(db, "Events.Handler.Invoke").Select(h => h.Symbol));

        Assert.Equal(new[] { DictionaryOfLists }, RefsQuery.Run(db, "Dictionary").Select(h => h.Symbol));
        Assert.Equal(new[] { DictionaryOfLists }, RefsQuery.Run(db, "Generic.Dictionary").Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_MatchesAGenericMethodWhoseTypeArgumentIsATuple()
    {
        // A tuple type argument carries a comma and a pair of parentheses inside the
        // angle brackets, so the parameter list is no longer the first '(' in the name.
        using var db = TypeArgumentDb();

        Assert.Contains(RunWithAuditTuple, RefsQuery.Run(db, "RunWithAuditAsync").Select(h => h.Symbol));
        Assert.Contains(RunWithAuditTuple,
            RefsQuery.Run(db, "AuditRunner.RunWithAuditAsync").Select(h => h.Symbol));
        Assert.Contains(RunWithAuditTuple, RefsQuery.Run(db, RunWithAuditTuple).Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_DoesNotMatchATypeArgumentAsThoughItWereTheSymbolItself()
    {
        // A type argument names a different symbol, which has its own occurrence at its
        // own position. Counting ILogger<PerfumeService> as an occurrence of
        // PerfumeService would double-count it and put the noise back.
        using var db = TypeArgumentDb();

        Assert.Empty(RefsQuery.Run(db, "PerfumeService"));
        Assert.Empty(RefsQuery.Run(db, "List"));
        Assert.Empty(RefsQuery.Run(db, "Func"));
        Assert.Empty(RefsQuery.Run(db, "Int32"));
        Assert.Empty(RefsQuery.Run(db, "Services.PerfumeService"));
    }

    [Fact]
    public void Refs_ALocalInsideAGenericMethodKeepsItsOwnName()
    {
        using var db = TypeArgumentDb();

        Assert.Equal(new[] { RunWithAudit + ".attempt" }, RefsQuery.Run(db, "attempt").Select(h => h.Symbol));
        Assert.Equal(new[] { RunWithAudit + ".attempt" },
            RefsQuery.Run(db, "RunWithAuditAsync.attempt").Select(h => h.Symbol));

        // And is not the method, exactly as a parameter is not the type.
        Assert.DoesNotContain(RunWithAudit + ".attempt",
            RefsQuery.Run(db, "RunWithAuditAsync").Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_WhenTheAngleBracketsDoNotBalance_TheStoredNameIsLeftAlone()
    {
        // 'Money.operator <(Money, Note)' contains a '<' that opens nothing. Counting
        // brackets without checking they pair swallows the rest of the name, and
        // 'operator >' is worse: the count goes negative and the parameter list is
        // promoted into the name, so `refs Note` answers with the operator as though
        // the type in its signature were the symbol.
        using var db = TypeArgumentDb();

        Assert.Empty(RefsQuery.Run(db, "Note"));
        Assert.Empty(RefsQuery.Run(db, "Models.Note"));
        Assert.Equal(new[] { "App.Models.Money" }, RefsQuery.Run(db, "Money").Select(h => h.Symbol));
    }

    [Fact]
    public void Refs_AcrossATypeArgumentList_IsStillCaseSensitiveAndStillHasNoWildcards()
    {
        using var db = TypeArgumentDb();

        Assert.Empty(RefsQuery.Run(db, "ilogger"));
        Assert.Empty(RefsQuery.Run(db, "ILOGGER"));
        Assert.Empty(RefsQuery.Run(db, "runWithAuditAsync"));

        // '_' would be "any one character" and '%' "any run of characters" if a pattern
        // language had crept back in with the type argument handling.
        Assert.Empty(RefsQuery.Run(db, "ILogge_"));
        Assert.Empty(RefsQuery.Run(db, "Dictionar_"));
        Assert.Empty(RefsQuery.Run(db, "%"));
        Assert.Empty(RefsQuery.Run(db, "%ILogger"));
    }

    [Fact]
    public void Impact_FindsCallersOfAGenericMethodWhateverItsTypeArguments()
    {
        // Where the under-count costs most: a blast radius sized from a query that
        // matched none of the method's instantiations reads as "nothing calls this".
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Auditing/Caller.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Auditing.Caller.Run()', 1, 30, 4, 40, 5),
                    (1, '{RunWithAudit}', 0, 32, 12, NULL, NULL),
                    (1, '{RunWithAuditTuple}', 0, 33, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(new[] { "App.Auditing.Caller.Run()" },
            ImpactQuery.Run(db, "RunWithAuditAsync").Select(h => h.Symbol));
        Assert.Equal(2, ImpactQuery.MatchedSymbols(db, "RunWithAuditAsync").Sum(tally => tally.Count));
    }

    [Fact]
    public void Ambiguity_SuggestsANameWithoutTypeArguments_BecauseThatIsWhatTheQueryReads()
    {
        // The block's whole value is that the pattern it prints can be typed back in.
        // Splitting a constructed generic on '.' produces segments like 'ILogger<App'
        // and 'PerfumeService>', so the suggestion was a fragment of a type argument.
        var tally = new[]
        {
            new SymbolTally(LoggerOfService, 5),
            new SymbolTally("App.Diagnostics.ILogger", 2)
        };

        var block = Ambiguity.RenderOccurrences("ILogger", tally);

        Assert.Contains("'Logging.ILogger'", block, StringComparison.Ordinal);
        Assert.DoesNotContain("'PerfumeService>'", block, StringComparison.Ordinal);
    }

    // ---- The ambiguity tally does not mistake constructions for symbols --------
    //
    // Recall for a generic symbol comes from matching a bare name through its type
    // argument list (above), and the ambiguity block tallies its answer by the exact
    // stored name. Put those two together and every construction of one generic type
    // becomes its own row: on the real solution `refs ILogger` reported "271 distinct
    // symbols" for 271 constructions of the one type ILogger<T>, with a "(+261 further
    // symbol(s))" tail and a narrowing suggestion no trailing run of segments could
    // satisfy, because there was no second symbol to narrow away from.
    //
    // So the tally is grouped a second time, by the stored name with only the
    // declaring segment's own type argument list removed and any parameter list left
    // exactly as it is. That merges ILogger<A> with ILogger<B>, and merges the two
    // instantiations of one generic method, while Publish(Perfume) and Publish(Note)
    // - genuine overloads sharing a name - stay apart, because neither carries a type
    // argument list of its own to remove.

    [Fact]
    public void WithoutDeclaringTypeArguments_StripsOnlyTheOwnTypeArgumentsOfAGenericTypeOrMethod()
    {
        Assert.Equal("App.Logging.ILogger", QueryHelper.WithoutDeclaringTypeArguments(LoggerOfService));
        Assert.Equal("App.Logging.ILogger", QueryHelper.WithoutDeclaringTypeArguments(LoggerOfList));

        // A tuple type argument puts a comma and a matched pair of parentheses inside
        // the angle brackets; the parameter list after them is untouched either way.
        Assert.Equal(
            "App.Auditing.AuditRunner.RunWithAuditAsync(System.String)",
            QueryHelper.WithoutDeclaringTypeArguments(RunWithAudit));
        Assert.Equal(
            "App.Auditing.AuditRunner.RunWithAuditAsync(System.String)",
            QueryHelper.WithoutDeclaringTypeArguments(RunWithAuditTuple));
    }

    [Fact]
    public void WithoutDeclaringTypeArguments_LeavesTheParameterListVerbatim_EvenAGenericOne()
    {
        // The parameter list's own generic argument is not the declaring segment's, so
        // it stays exactly as stored - the whole point being that merging constructions
        // of a type or method must never merge genuinely different signatures.
        const string method =
            "App.Services.PerfumeRepository.Find(System.Collections.Generic.List<App.Models.Perfume>)";
        Assert.Equal(method, QueryHelper.WithoutDeclaringTypeArguments(method));
    }

    [Fact]
    public void WithoutDeclaringTypeArguments_KeepsGenuineOverloadsDistinct()
    {
        const string publishPerfume = "App.Services.PerfumeService.Publish(App.Models.Perfume)";
        const string publishNote = "App.Services.PerfumeService.Publish(App.Models.Note)";

        Assert.Equal(publishPerfume, QueryHelper.WithoutDeclaringTypeArguments(publishPerfume));
        Assert.Equal(publishNote, QueryHelper.WithoutDeclaringTypeArguments(publishNote));
    }

    [Fact]
    public void WithoutDeclaringTypeArguments_LeavesAnUnbalancedOperatorNameAlone()
    {
        // 'operator <' and 'operator >' contain an angle bracket that opens or closes
        // nothing. Reading it as a type argument list would swallow the parameter list
        // that follows, exactly as it would for WithoutTypeArguments.
        Assert.Equal(LessThan, QueryHelper.WithoutDeclaringTypeArguments(LessThan));
        Assert.Equal(GreaterThan, QueryHelper.WithoutDeclaringTypeArguments(GreaterThan));
    }

    [Fact]
    public void WithoutDeclaringTypeArguments_LeavesAGenericEnclosingTypesArgumentsAlone()
    {
        // Handler<T> is not the declaring segment here - Invoke is, and Invoke has no
        // type arguments of its own to remove. Only the rightmost segment's own
        // construction is ever merged away.
        Assert.Equal(HandlerInvoke, QueryHelper.WithoutDeclaringTypeArguments(HandlerInvoke));
    }

    [Fact]
    public void Ambiguity_MergesConstructionsOfOneGenericTypeIntoOneRow()
    {
        // The motivating real case, built from the real fixture: ILogger, ILogger<T>
        // and ILogger<List<T>> are one type, not three, whatever refs ILogger matches.
        using var db = TypeArgumentDb();

        var tally = Ambiguity.Of(RefsQuery.Run(db, "ILogger"));

        var row = Assert.Single(tally);
        Assert.Equal("App.Logging.ILogger", row.Symbol);
        Assert.Equal(3, row.Count);
        Assert.Equal(3, row.Constructions);
    }

    [Fact]
    public void Ordered_MergesInstantiationsOfOneGenericMethodIntoOneRow()
    {
        var raw = new[]
        {
            new SymbolTally(RunWithAudit, 20),
            new SymbolTally(RunWithAuditTuple, 13)
        };

        var merged = Ambiguity.Ordered(raw);

        var row = Assert.Single(merged);
        Assert.Equal("App.Auditing.AuditRunner.RunWithAuditAsync(System.String)", row.Symbol);
        Assert.Equal(33, row.Count);
        Assert.Equal(2, row.Constructions);
    }

    [Fact]
    public void Ordered_KeepsGenuineOverloadsAsSeparateRows()
    {
        const string publishPerfume = "App.Services.PerfumeService.Publish(App.Models.Perfume)";
        const string publishNote = "App.Services.PerfumeService.Publish(App.Models.Note)";

        var raw = new[]
        {
            new SymbolTally(publishPerfume, 4),
            new SymbolTally(publishNote, 1)
        };

        var merged = Ambiguity.Ordered(raw);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, tally => Assert.Equal(1, tally.Constructions));
        Assert.Contains(merged, t => t.Symbol == publishPerfume && t.Count == 4);
        Assert.Contains(merged, t => t.Symbol == publishNote && t.Count == 1);
    }

    [Fact]
    public void RenderOccurrences_ReportsConstructionsSeparatelyFromDistinctSymbols()
    {
        // Matches the example in the brief this fixes: several results spanning a
        // handful of distinct symbols, some of which are several constructions of one
        // generic type. Both numbers are reported, and neither one alone would do:
        // the first without the second still looks like N different things, and the
        // second alone would not decompose the reported total.
        var tally = Ambiguity.Ordered(new[]
        {
            new SymbolTally(LoggerOfService, 3),
            new SymbolTally(LoggerOfList, 2),
            new SymbolTally("App.Diagnostics.ILogger", 1)
        });

        var block = Ambiguity.RenderOccurrences("ILogger", tally);

        Assert.Contains("2 distinct symbols across 3 construction(s)", block, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderOccurrences_OmitsTheConstructionsClause_WhenNothingWasMerged()
    {
        // No crying wolf: when every distinct symbol is exactly one construction,
        // saying so adds nothing, so the reporting-only change must not print it.
        var tally = Ambiguity.Ordered(new[]
        {
            new SymbolTally("App.Models.Perfume.Status", 3),
            new SymbolTally("App.Models.OrderStatus", 1)
        });

        var block = Ambiguity.RenderOccurrences("Status", tally);

        Assert.DoesNotContain("construction", block, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCallers_AlsoReportsConstructionsSeparatelyFromDistinctSymbols()
    {
        var tally = Ambiguity.Ordered(new[]
        {
            new SymbolTally(LoggerOfService, 3),
            new SymbolTally(LoggerOfList, 2),
            new SymbolTally("App.Diagnostics.ILogger", 1)
        });

        var block = Ambiguity.RenderCallers("ILogger", tally, anyCallers: true);

        Assert.Contains("2 distinct symbols across 3 construction(s)", block, StringComparison.Ordinal);
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

    // ---- An empty answer must not contradict the suppression footer -------
    //
    // refs and impact suppress generated documents and then print "N further result(s)
    // in generated code". When every hit was suppressed, the answer read
    //
    //     No symbol matching 'X' is in the index ... nothing of that name was indexed
    //     3 further result(s) in generated code.
    //
    // Both sentences cannot be true, and the one an agent acts on is the first: it
    // concludes the symbol does not exist and deletes it. This is reachable on the
    // flagship case, a Razor page member whose only declaration is in generated code.

    /// <summary>
    /// A Razor page whose members exist only in the generator's output: ViewData is
    /// defined and used there and nowhere openable, Title is defined there and used
    /// nowhere at all, and the one occurrence on disk is of an unrelated symbol.
    /// </summary>
    private static SqliteConnection GeneratedOnlyDb()
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
                (2, 'AspNetCore.Pages_Index.ExecuteAsync()', 1,  5, 8,   30,    9),
                (2, 'AspNetCore.Pages_Index.ViewData',       1,  6, 8,    6,   20),
                (2, 'AspNetCore.Pages_Index.ViewData',       0,  9, 8, NULL, NULL),
                (2, 'AspNetCore.Pages_Index.ViewData',       0, 11, 8, NULL, NULL),
                (2, 'AspNetCore.Pages_Index.Title',          1,  7, 8,    7,   18),
                (1, 'App.Models.Perfume.Status',             0,  2, 4, NULL, NULL);
            INSERT INTO symbol_fts(symbol) VALUES
                ('AspNetCore.Pages_Index.ViewData'), ('AspNetCore.Pages_Index.Title');
            """;
        cmd.ExecuteNonQuery();
        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
        return db;
    }

    [Fact]
    public void Refs_WhenEveryOccurrenceWasSuppressedAsGenerated_SaysTheSymbolIsIndexedRatherThanAbsent()
    {
        using var db = GeneratedOnlyDb();
        const string pattern = "ViewData";

        Assert.Empty(RefsQuery.Run(db, pattern));
        Assert.Equal(3, RefsQuery.CountInGeneratedCode(db, pattern));

        var explanation = RefsQuery.ExplainEmpty(db, pattern);

        // The sentence that does the damage, said of a symbol that is in the index.
        Assert.DoesNotContain("nothing of that name was indexed", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("No symbol matching", explanation, StringComparison.Ordinal);

        Assert.Contains(pattern, explanation, StringComparison.Ordinal);
        Assert.Contains("is in the index", explanation, StringComparison.Ordinal);
        Assert.Contains("generated", explanation, StringComparison.Ordinal);
        Assert.Contains("--include-generated", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_OnAnIndexWithGeneratedCode_StillCallsAGenuinelyAbsentSymbolAbsent()
    {
        // The other half: the suppression branch must not swallow the real absence.
        using var db = GeneratedOnlyDb();
        const string pattern = "Zzzyzx";

        Assert.Empty(RefsQuery.Run(db, pattern));
        Assert.Equal(0, RefsQuery.CountInGeneratedCode(db, pattern));

        var explanation = RefsQuery.ExplainEmpty(db, pattern);

        Assert.Contains("No symbol matching", explanation, StringComparison.Ordinal);
        Assert.Contains("nothing of that name was indexed", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Impact_WhenEveryCallerWasSuppressedAsGenerated_SaysSoRatherThanClaimingTheSymbolIsAbsent()
    {
        using var db = GeneratedOnlyDb();
        const string pattern = "ViewData";

        Assert.Empty(ImpactQuery.Run(db, pattern));
        Assert.Equal(1, ImpactQuery.CountInGeneratedCode(db, pattern));

        var explanation = ImpactQuery.ExplainEmpty(db, pattern);

        Assert.DoesNotContain("nothing of that name was indexed", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("No symbol matching", explanation, StringComparison.Ordinal);
        Assert.Contains("generated", explanation, StringComparison.Ordinal);
        Assert.Contains("--include-generated", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Impact_WhenEveryOccurrenceIsGenerated_SaysSoRatherThanBlamingAMissingBodyRange()
    {
        // Title is defined in the generator's output and referred to nowhere. Reasoning
        // over unfiltered occurrences reached "references are in the index but none
        // falls inside a recorded body range", which names the wrong reason, and
        // reasoning over none of them at all would have claimed the symbol is absent.
        using var db = GeneratedOnlyDb();
        const string pattern = "Title";

        Assert.Empty(ImpactQuery.Run(db, pattern));
        Assert.Equal(0, ImpactQuery.CountInGeneratedCode(db, pattern));

        var explanation = ImpactQuery.ExplainEmpty(db, pattern);

        Assert.DoesNotContain("nothing of that name was indexed", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("No symbol matching", explanation, StringComparison.Ordinal);
        Assert.Contains("is in the index", explanation, StringComparison.Ordinal);
        Assert.Contains("--include-generated", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Impact_OnAnIndexWithGeneratedCode_StillCallsAGenuinelyAbsentSymbolAbsent()
    {
        using var db = GeneratedOnlyDb();

        var explanation = ImpactQuery.ExplainEmpty(db, "Zzzyzx");

        Assert.Contains("No symbol matching", explanation, StringComparison.Ordinal);
        Assert.Contains("nothing of that name was indexed", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void DefAndOutline_SuppressNothing_SoTheirEmptyExplanationsAreUnchanged()
    {
        // Confirmed rather than assumed: def and outline always include generated
        // documents, so they can never reach the state this fix is about. They answer
        // for the generated-only member instead of explaining an empty result, and
        // when they are empty it is for the reasons they already gave.
        using var db = GeneratedOnlyDb();

        Assert.NotEmpty(DefQuery.Run(db, "ViewData"));
        Assert.NotEmpty(OutlineQuery.Run(db, "App/obj/Debug/net10.0/generated/Pages_Index_cshtml.g.cs"));

        Assert.Contains("No symbol matching", DefQuery.ExplainEmpty(db, "Zzzyzx"), StringComparison.Ordinal);
        Assert.Contains("No document with the path",
            OutlineQuery.ExplainEmpty(db, "App/Pages/Missing.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefsFromTheCommandLine_WhenEverythingWasSuppressed_DoesNotContradictItsOwnFooter()
    {
        // The two statements are printed by different code paths, so the only place the
        // contradiction is visible is the whole rendered answer.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteIndexFileWhollyGenerated(indexPath);

        var result = await InvokeAsync("refs", "ViewData", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0 result(s)", result.Output, StringComparison.Ordinal);
        Assert.Contains("3 further result(s) in generated code", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing of that name was indexed", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("No symbol matching", result.Output, StringComparison.Ordinal);
        Assert.Contains("is in the index", result.Output, StringComparison.Ordinal);
        Assert.Contains("--include-generated", result.Output, StringComparison.Ordinal);

        // And asking for them produces the hits the explanation promised.
        var included = await InvokeAsync("refs", "ViewData", "--include-generated", "--solution", solution);
        Assert.Equal(0, included.ExitCode);
        Assert.Contains("3 result(s)", included.Output, StringComparison.Ordinal);
    }

    // ---- The schema this build understands ---------------------------------
    //
    // The index is a cache, and OpenIndex opened whatever file was there. Adding the
    // document.generated column therefore turned every cache built before it into a
    // raw "SqliteException: no such column: d.generated" from every verb, which tells
    // the user nothing about what to do. Constraint 3 again: a schema vela cannot read
    // must announce itself, not surface as a stack trace or, worse, as a partial answer.

    [Fact]
    public void SchemaCreate_StampsTheVersionThisBuildReads()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Schema.Create(db);

        Assert.Equal(Schema.Version, Schema.ReadVersion(db));
        Assert.True(Schema.Version > 0, "an unstamped database reads 0, so 0 cannot be a valid version");
    }

    [Fact]
    public async Task AnIndexBuiltBeforeTheGeneratedColumn_SaysToReindexInsteadOfThrowingRawSql()
    {
        // The real developer cache, reproduced: the schema as it stood before the
        // generated column, with no version stamp on it. Every verb selects
        // d.generated, so without a guard this is SqliteException, uncaught, with
        // EnableDefaultExceptionHandler off, and the test fails as a thrown exception.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WritePreviousSchemaIndexFile(indexPath);

        var result = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);

        Assert.Equal(Program.ExitCannotAnswer, result.ExitCode);
        Assert.DoesNotContain("no such column", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schema", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vela index", result.Output, StringComparison.Ordinal);

        // find opens the same index by the same route, so it carries the same answer.
        var find = await InvokeAsync("find", "Status", "--solution", solution);
        Assert.Equal(Program.ExitCannotAnswer, find.ExitCode);
        Assert.Contains("vela index", find.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIndexAtTheCurrentSchemaVersionOpens_AndOneAtAnyOtherVersionDoesNot()
    {
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteIndexFile(indexPath, new HealthRecord(DateTime.UtcNow, null, false, null));

        var current = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(0, current.ExitCode);
        Assert.Contains("App/Models/Perfume.cs", current.Output, StringComparison.Ordinal);

        // Same tables, different stamp. A future schema is as unreadable as a past one,
        // so the guard is a mismatch test rather than a "too old" test.
        SetSchemaVersion(indexPath, Schema.Version + 1);

        var mismatched = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(Program.ExitCannotAnswer, mismatched.ExitCode);
        Assert.Contains("schema", mismatched.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vela index", mismatched.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("App/Models/Perfume.cs", mismatched.Output, StringComparison.Ordinal);
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

    // ---- A bare name must never silently merge distinct symbols -----------
    //
    // Measured on a real 375,608-line solution: `vela refs Perfume` answered 3,104
    // results, which is MORE than `grep -w Perfume` answers there (1,897). Matching is
    // by whole dotted segment, so every symbol whose last segment is Perfume matches,
    // and the single number at the foot of the answer merged at least four distinct
    // symbols: the entity, the entity's constructor, an enum member, and a property of
    // an unrelated API response type.
    //
    // Every hit is real. The COUNT describes something that does not exist, and an agent
    // sizing a change reads that count. The whole pitch of this tool is replacing grep's
    // noise with compiler-exact answers, so a headline number that conflates four
    // symbols is precisely the failure it exists to prevent.
    //
    // The fix is reporting and nothing else. The same rows come back in the same order:
    // suppressing, ranking or scoring them would be the heuristic behaviour Constraint 1
    // forbids. The answer simply says what it spans, and how to ask about one of them.
    //
    // And it must not cry wolf. A pattern resolving to one symbol prints no notice at
    // all: a warning on every query is a warning nobody reads, which this project
    // already learned once from the degraded-index banner.

    [Fact]
    public async Task Refs_WhenABareNameMatchesSeveralDistinctSymbols_NamesEachWithItsOwnCount_MostHitsFirst()
    {
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousIndexFile(indexPath);

        var result = await InvokeAsync("refs", "Perfume", "--solution", solution);

        Assert.Equal(0, result.ExitCode);

        // Constraint 1: not one row fewer than before. This is a reporting change.
        Assert.Equal(8, ReportedTotal(result.Output));

        Assert.Contains("'Perfume' is ambiguous", result.Output, StringComparison.Ordinal);
        Assert.Contains("4 distinct symbols", result.Output, StringComparison.Ordinal);

        // Ordering is total and deterministic, and never left to SQLite: most hits
        // first, ties broken by symbol name ordinally so the same index answers the
        // same way on every machine whatever the current culture.
        Assert.Equal(
            new[]
            {
                (3, "App.Data.Entities.Perfume"),
                (2, "App.Data.Enums.EntityType.Perfume"),
                (2, "App.ServiceModel.Api.FragranticaPerfumeDetailResponse.Perfume"),
                (1, "App.Data.Entities.Perfume.Perfume()")
            },
            AmbiguityRows(result.Output));
    }

    [Fact]
    public async Task Refs_TheAmbiguityCountsAddUpToTheReportedTotal()
    {
        // The block exists to explain the total, so a reader has to be able to check it
        // against the total. If the two ever disagree the block is worse than nothing:
        // it would be a second confident number describing the same answer.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousIndexFile(indexPath);

        var result = await InvokeAsync("refs", "Perfume", "--solution", solution);

        var rows = AmbiguityRows(result.Output);
        Assert.NotEmpty(rows);
        Assert.Equal(ReportedTotal(result.Output), rows.Sum(r => r.Count));
    }

    [Fact]
    public async Task Refs_WhenThePatternResolvesToOneSymbol_PrintsNoAmbiguityNotice()
    {
        // No crying wolf. Perfume.Status matches exactly one symbol, so the total IS a
        // count of that symbol and there is nothing to warn about. A notice here would
        // train the reader to skip the notice in the case that matters.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousIndexFile(indexPath);

        var result = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, ReportedTotal(result.Output));
        Assert.DoesNotContain("ambiguous", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct symbols", result.Output, StringComparison.Ordinal);
        Assert.Empty(AmbiguityRows(result.Output));
    }

    [Fact]
    public async Task Refs_TheMoreQualifiedPatternItSuggests_ReallyResolvesToOneSymbol()
    {
        // Telling the reader the name is ambiguous without telling them what to type
        // instead leaves them where they started. The suggestion is derived from the
        // matched names, so this feeds it back through the real query and checks it
        // does what the sentence promises.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousIndexFile(indexPath);

        var ambiguous = await InvokeAsync("refs", "Perfume", "--solution", solution);
        Assert.Contains("'Entities.Perfume'", ambiguous.Output, StringComparison.Ordinal);

        var narrowed = await InvokeAsync("refs", "Entities.Perfume", "--solution", solution);

        Assert.Equal(0, narrowed.ExitCode);
        Assert.Equal(3, ReportedTotal(narrowed.Output));
        Assert.DoesNotContain("ambiguous", narrowed.Output, StringComparison.Ordinal);
        Assert.Empty(AmbiguityRows(narrowed.Output));
    }

    [Fact]
    public async Task Def_AlsoReportsAWholeSegmentNameThatMatchesSeveralSymbols()
    {
        // def takes a symbol pattern too, and its hits are occurrences of that pattern,
        // so it conflates in exactly the same way: `def Perfume` here answers with four
        // declarations of four different things.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousIndexFile(indexPath);

        var result = await InvokeAsync("def", "Perfume", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("'Perfume' is ambiguous", result.Output, StringComparison.Ordinal);

        // Every count is 1 here, so the ordering is settled entirely by the ordinal
        // symbol name: without that second key SQLite's row order would decide it.
        Assert.Equal(
            new[]
            {
                (1, "App.Data.Entities.Perfume"),
                (1, "App.Data.Entities.Perfume.Perfume()"),
                (1, "App.Data.Enums.EntityType.Perfume"),
                (1, "App.ServiceModel.Api.FragranticaPerfumeDetailResponse.Perfume")
            },
            AmbiguityRows(result.Output));

        Assert.Equal(ReportedTotal(result.Output), AmbiguityRows(result.Output).Sum(r => r.Count));
    }

    [Fact]
    public async Task Outline_NeverReportsAmbiguity_BecauseItsArgumentIsAPathAndNotAName()
    {
        // outline takes a file path, and a file defines many symbols by definition. Its
        // hits are not occurrences of the argument at all, so the notice would fire on
        // every outline of every file that declares more than one thing: the loudest
        // possible way of crying wolf.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousIndexFile(indexPath);

        var result = await InvokeAsync("outline", "App/Data/Entities/Perfume.cs", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5, ReportedTotal(result.Output));
        Assert.DoesNotContain("ambiguous", result.Output, StringComparison.Ordinal);
        Assert.Empty(AmbiguityRows(result.Output));
    }

    [Fact]
    public async Task Impact_WhenABareNameMatchesSeveralSymbols_SaysTheCallersAreNotOneSymbolsBlastRadius()
    {
        // impact conflates as badly as refs and the consequence is worse, because a
        // blast radius is what an agent sizes a change from. Its rows name the CALLERS
        // rather than occurrences of the pattern, so the tally cannot be read off the
        // answer: it has to name the symbols the pattern matched, and say plainly that
        // the numbers are references searched rather than callers found.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousCallerIndexFile(indexPath);

        var result = await InvokeAsync("impact", "Perfume", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, ReportedTotal(result.Output));

        Assert.Contains("'Perfume' is ambiguous", result.Output, StringComparison.Ordinal);
        Assert.Contains("2 distinct symbols", result.Output, StringComparison.Ordinal);
        Assert.Contains("not how many callers it has", result.Output, StringComparison.Ordinal);

        Assert.Equal(
            new[]
            {
                (2, "App.Data.Entities.Perfume"),
                (1, "App.Data.Enums.EntityType.Perfume")
            },
            AmbiguityRows(result.Output));
    }

    [Fact]
    public async Task Impact_WhenThePatternResolvesToOneSymbol_PrintsNoAmbiguityNotice()
    {
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousCallerIndexFile(indexPath);

        var result = await InvokeAsync("impact", "Perfume.Status", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, ReportedTotal(result.Output));
        Assert.DoesNotContain("ambiguous", result.Output, StringComparison.Ordinal);
        Assert.Empty(AmbiguityRows(result.Output));
    }

    [Fact]
    public async Task Refs_WhenTheNameMatchesFarTooManySymbolsToList_SummarisesTheTail_AndStillAddsUp()
    {
        // Measured on the real solution: `refs Name` matches 426 distinct symbols and
        // `refs Id` matches 440. Listing all of them puts the header sentence hundreds of
        // lines above the footer, so the one thing the reader needs - "this total is not
        // one symbol" - scrolls off the end of the answer, and a caller reading the tail
        // of the output sees a list of one-hit symbols and no explanation of what it is.
        // A block nobody can read is the degraded-index banner's mistake made again.
        //
        // So the tail is summarised rather than dropped. The count on that line is the
        // hits of every symbol not listed, which keeps the arithmetic exact: the block
        // still adds up to the reported total, which is the property that lets a reader
        // check it at all.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);

        // 14 symbols, the nth with n hits, so 105 hits in total.
        WriteManySymbolIndexFile(indexPath, symbols: 14);

        var result = await InvokeAsync("refs", "Thing", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(105, ReportedTotal(result.Output));

        // Nothing is understated: the header still says how many there really are.
        Assert.Contains("14 distinct symbols", result.Output, StringComparison.Ordinal);

        var rows = AmbiguityRows(result.Output);
        Assert.Equal(11, rows.Count);
        Assert.Equal((14, "App.N14.Thing"), rows[0]);
        Assert.Equal((5, "App.N05.Thing"), rows[9]);
        Assert.Equal((10, "(+4 further symbol(s))"), rows[10]);

        Assert.Equal(ReportedTotal(result.Output), rows.Sum(row => row.Count));

        // And the whole block, header and footer included, is short enough to survive
        // being read from the end of the answer, which is how it is read.
        var block = result.Output[result.Output.IndexOf("'Thing' is ambiguous", StringComparison.Ordinal)..];
        Assert.True(block.Split('\n').Length <= 20, block);
    }

    [Fact]
    public async Task Refs_TheAmbiguityBlockCountsTheResultsItShowed_AndNotTheWholeIndex()
    {
        // The tally is taken from the rows the answer printed, and refs leaves
        // generated documents out by default, so a symbol whose every occurrence is in
        // the Razor generator's output is not in it. "it matches N distinct symbols"
        // was therefore a claim about the index made from a filtered view of it: on the
        // real solution `refs Name` said 426 where `refs Name --include-generated` said
        // 427. A filtered view that describes itself as the whole is the failure this
        // tool exists to prevent, so the sentence says what it counted, and the answer
        // still declares what it left out.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguityAcrossGeneratedCodeIndexFile(indexPath);

        var shown = await InvokeAsync("refs", "Perfume", "--solution", solution);

        Assert.Equal(0, shown.ExitCode);
        Assert.Equal(5, ReportedTotal(shown.Output));
        Assert.Contains("the 5 result(s) above span 2 distinct symbols", shown.Output, StringComparison.Ordinal);

        // The claim it must not make: the third symbol exists, and this answer cannot
        // see it.
        Assert.DoesNotContain("matches 2 distinct symbols", shown.Output, StringComparison.Ordinal);
        Assert.Contains("2 further result(s) in generated code", shown.Output, StringComparison.Ordinal);
        Assert.Equal(ReportedTotal(shown.Output), AmbiguityRows(shown.Output).Sum(row => row.Count));

        // And asked for the whole index, it says three, so the number really was a
        // property of the view rather than of the code.
        var all = await InvokeAsync("refs", "Perfume", "--include-generated", "--solution", solution);

        Assert.Equal(0, all.ExitCode);
        Assert.Equal(7, ReportedTotal(all.Output));
        Assert.Contains("the 7 result(s) above span 3 distinct symbols", all.Output, StringComparison.Ordinal);
        Assert.Contains("AspNetCore.Pages_Index.Perfume", all.Output, StringComparison.Ordinal);
        Assert.Equal(ReportedTotal(all.Output), AmbiguityRows(all.Output).Sum(row => row.Count));
    }

    [Fact]
    public async Task Refs_TheGeneratedCodeLineStaysWithTheCountItQualifies_AndTheBlockComesLast()
    {
        // "N further result(s) in generated code" qualifies the result count, and it
        // was printed after the ambiguity block, which put a screen of symbol names
        // between a number and the sentence that corrects it.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguityAcrossGeneratedCodeIndexFile(indexPath);

        var output = (await InvokeAsync("refs", "Perfume", "--solution", solution)).Output;

        var count = output.IndexOf("5 result(s)", StringComparison.Ordinal);
        var generated = output.IndexOf("2 further result(s) in generated code", StringComparison.Ordinal);
        var block = output.IndexOf("is ambiguous", StringComparison.Ordinal);

        Assert.True(count < generated, output);
        Assert.True(generated < block, output);
    }

    [Fact]
    public async Task Impact_WhenAnAmbiguousPatternHasNoCallersAtAll_StillSaysTheAnswerSpansSeveralSymbols()
    {
        // The block was printed only when there were callers to qualify, so an
        // ambiguous pattern with none fell through to an explanation written in the
        // singular: "references to 'Perfume' are in the index, but none of them falls
        // inside a definition whose body range was recorded". That reads as a statement
        // about one symbol, and it is the whole answer.
        using var repo = new TempDirectory();
        var solution = Path.Combine(repo.Path, "App.sln");
        File.WriteAllText(solution, "");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);
        WriteAmbiguousWithoutCallersIndexFile(indexPath);

        var result = await InvokeAsync("impact", "Perfume", "--solution", solution);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, ReportedTotal(result.Output));

        Assert.Contains("'Perfume' is ambiguous", result.Output, StringComparison.Ordinal);
        Assert.Contains("2 distinct symbols", result.Output, StringComparison.Ordinal);
        Assert.Equal(
            new[]
            {
                (2, "App.Data.Entities.Perfume"),
                (1, "App.Data.Enums.EntityType.Perfume")
            },
            AmbiguityRows(result.Output));

        // The explanation is still there, and the block corrects it rather than
        // replacing it.
        var explanation = result.Output.IndexOf("no calling symbol can be named", StringComparison.Ordinal);
        Assert.True(explanation >= 0, result.Output);
        Assert.True(explanation < result.Output.IndexOf("is ambiguous", StringComparison.Ordinal), result.Output);
    }

    /// <summary>
    /// The count and symbol on each line of a rendered ambiguity block, read back out of
    /// the output the caller actually sees. Hit lines cannot match: they carry a
    /// "line:column" pair where this expects a bare count followed by two spaces.
    /// </summary>
    private static IReadOnlyList<(int Count, string Symbol)> AmbiguityRows(string output) =>
        Regex.Matches(output, @"^ {2,}(\d+)  (\S.*?)\s*$", RegexOptions.Multiline)
             .Select(m => (int.Parse(m.Groups[1].Value), m.Groups[2].Value))
             .ToList();

    /// <summary>The "N result(s)" line, which is the number the block has to explain.</summary>
    private static int ReportedTotal(string output)
    {
        var match = Regex.Match(output, @"^(\d+) result\(s\)\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, "no result count in the output:\n" + output);
        return int.Parse(match.Groups[1].Value);
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
    public async Task AnEditAboveTheSolutionDirectoryButInsideTheRepository_ReportsDegraded()
    {
        // The index covers the repository root, so a `repo/src/App.sln` layout indexes
        // `repo/tests/` too. Staleness walked the SOLUTION directory, which is
        // `repo/src`, so editing a test file left every verb answering exit 0 with no
        // banner, at line numbers that had moved. The exposure did not exist before the
        // root widened, because those files could not previously be in the index at all.
        // The watch has to cover exactly what the index covers, or Constraint 3 is
        // breached by the half of the repository nobody is watching.
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, ".git"));

        var solutionDirectory = Path.Combine(repo.Path, "src");
        Directory.CreateDirectory(solutionDirectory);
        var solution = Path.Combine(solutionDirectory, "App.sln");
        File.WriteAllText(solution, "");

        var test = Path.Combine(repo.Path, "tests", "AppTests", "PerfumeTests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, "public class PerfumeTests { }");

        using var cache = new TempDirectory();
        using var _ = new CacheHome(cache.Path);

        var indexPath = IndexPaths.ForSolution(solution);
        IndexPaths.EnsureDirectoryExists(indexPath);

        var builtAt = DateTime.UtcNow;
        WriteIndexFile(indexPath, new HealthRecord(builtAt, null, false, null));

        // Nothing has changed since the build, so nothing is claimed. This also pins
        // that widening the walk did not make every query stale by default.
        var fresh = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(0, fresh.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", fresh.Output, StringComparison.Ordinal);

        File.SetLastWriteTimeUtc(test, builtAt.AddMinutes(5));

        var stale = await InvokeAsync("refs", "Perfume.Status", "--solution", solution);
        Assert.Equal(IndexHealth.ExitDegraded, stale.ExitCode);
        Assert.Contains("INCOMPLETE", stale.Output, StringComparison.Ordinal);
        Assert.Contains("stale", stale.Output, StringComparison.OrdinalIgnoreCase);

        // Named relative to the root the index was built against, which is the same
        // root every path in an answer is relative to. A '../tests/...' here would be a
        // path the reader cannot hand back to outline.
        Assert.Contains("tests/AppTests/PerfumeTests.cs", stale.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("../tests", stale.Output, StringComparison.Ordinal);
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

    /// <summary>
    /// An on-disk index in which every occurrence of ViewData is in the generator's
    /// output, which is what a Razor page member looks like.
    /// </summary>
    private static void WriteIndexFileWhollyGenerated(string path)
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
                    (2, 'AspNetCore.Pages_Index.ViewData', 1,  5, 8,    6,    9),
                    (2, 'AspNetCore.Pages_Index.ViewData', 0,  9, 8, NULL, NULL),
                    (2, 'AspNetCore.Pages_Index.ViewData', 0, 11, 8, NULL, NULL),
                    (1, 'App.Models.Perfume.Status',       0,  2, 4, NULL, NULL);
                INSERT INTO symbol_fts(symbol) VALUES ('AspNetCore.Pages_Index.ViewData');
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    /// <summary>
    /// The ScentVerdict shape, in miniature: four distinct symbols whose last dotted
    /// segment is Perfume - the entity, the entity's constructor, an enum member, and a
    /// property of an unrelated API response type - plus one member, Perfume.Status,
    /// that a qualified pattern resolves to on its own.
    /// </summary>
    private static void WriteAmbiguousIndexFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Data/Entities/Perfume.cs', 'csharp'),
                    (2, 'App/Pages/Index.cshtml', 'razor');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Data.Entities.Perfume',            1,  4, 13,   40,    1),
                    (1, 'App.Data.Entities.Perfume',            0, 12,  8, NULL, NULL),
                    (2, 'App.Data.Entities.Perfume',            0,  3,  6, NULL, NULL),
                    (1, 'App.Data.Entities.Perfume.Perfume()',  1,  6,  8,    8,    9),
                    (1, 'App.Data.Enums.EntityType.Perfume',    1, 20,  8, NULL, NULL),
                    (2, 'App.Data.Enums.EntityType.Perfume',    0,  9,  6, NULL, NULL),
                    (1, 'App.ServiceModel.Api.FragranticaPerfumeDetailResponse.Perfume', 1, 24, 8, NULL, NULL),
                    (2, 'App.ServiceModel.Api.FragranticaPerfumeDetailResponse.Perfume', 0, 10, 6, NULL, NULL),
                    (1, 'App.Data.Entities.Perfume.Status',     1, 30,  8, NULL, NULL),
                    (2, 'App.Data.Entities.Perfume.Status',     0, 11,  6, NULL, NULL);
                INSERT INTO symbol_fts(symbol) VALUES
                    ('App.Data.Entities.Perfume'),
                    ('App.Data.Entities.Perfume.Perfume()'),
                    ('App.Data.Enums.EntityType.Perfume'),
                    ('App.ServiceModel.Api.FragranticaPerfumeDetailResponse.Perfume'),
                    ('App.Data.Entities.Perfume.Status');
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    /// <summary>
    /// Two methods, between them referring to two different symbols called Perfume and
    /// to Perfume.Status. impact's rows name the methods, so the symbols the pattern
    /// matched appear nowhere in the answer unless the answer says so.
    /// </summary>
    private static void WriteAmbiguousCallerIndexFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Services/PerfumeService.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Services.PerfumeService.Publish()', 1, 30,  4,   40,    5),
                    (1, 'App.Services.PerfumeService.Archive()', 1, 50,  4,   60,    5),
                    (1, 'App.Data.Entities.Perfume',             0, 32, 12, NULL, NULL),
                    (1, 'App.Data.Entities.Perfume',             0, 33, 12, NULL, NULL),
                    (1, 'App.Data.Entities.Perfume.Status',      0, 34, 12, NULL, NULL),
                    (1, 'App.Data.Enums.EntityType.Perfume',     0, 52, 12, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    /// <summary>
    /// Three distinct symbols ending in the segment Perfume, the third of them known
    /// only from the Razor generator's output, which refs and impact leave out of their
    /// default answer. So the default view spans two and the index holds three, which is
    /// the case that makes "it matches N distinct symbols" a claim the tally cannot
    /// support.
    /// </summary>
    private static void WriteAmbiguityAcrossGeneratedCodeIndexFile(string path)
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
                    (1, 'App/Data/Entities/Perfume.cs', 'csharp', 0),
                    (2, 'App/Pages/Index.cshtml', 'razor', 0),
                    (3, 'App/obj/Debug/net10.0/generated/Pages_Index_cshtml.g.cs', 'csharp', 1);
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Data.Entities.Perfume',         1,  4, 13,   40,    1),
                    (1, 'App.Data.Entities.Perfume',         0, 12,  8, NULL, NULL),
                    (2, 'App.Data.Entities.Perfume',         0,  3,  6, NULL, NULL),
                    (1, 'App.Data.Enums.EntityType.Perfume', 1, 20,  8, NULL, NULL),
                    (2, 'App.Data.Enums.EntityType.Perfume', 0,  9,  6, NULL, NULL),
                    (3, 'AspNetCore.Pages_Index.Perfume',    1,  5,  8,    6,    9),
                    (3, 'AspNetCore.Pages_Index.Perfume',    0,  9,  8, NULL, NULL);
                INSERT INTO symbol_fts(symbol) VALUES
                    ('App.Data.Entities.Perfume'),
                    ('App.Data.Enums.EntityType.Perfume'),
                    ('AspNetCore.Pages_Index.Perfume');
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    /// <summary>
    /// Two distinct symbols ending in Perfume, referred to only from a Razor view,
    /// which has no recorded body range. So the references are real, the pattern is
    /// ambiguous, and impact can name no caller for either of them.
    /// </summary>
    private static void WriteAmbiguousWithoutCallersIndexFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        Schema.Create(db);

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Pages/Index.cshtml', 'razor');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Data.Entities.Perfume',         0, 3, 6, NULL, NULL),
                    (1, 'App.Data.Entities.Perfume',         0, 4, 6, NULL, NULL),
                    (1, 'App.Data.Enums.EntityType.Perfume', 0, 5, 6, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    /// <summary>
    /// An index in which a single bare name matches more distinct symbols than any block
    /// could sensibly list, which on the real solution is the ordinary case rather than
    /// the extreme one: `refs Name` matches 426 of them. The nth symbol carries n hits,
    /// so every count differs and the ordering is settled by count alone.
    /// </summary>
    private static void WriteManySymbolIndexFile(string path, int symbols)
    {
        if (File.Exists(path)) File.Delete(path);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        Schema.Create(db);

        using (var document = db.CreateCommand())
        {
            document.CommandText =
                "INSERT INTO document(id, relative_path, language) VALUES (1, 'App/Thing.cs', 'csharp')";
            document.ExecuteNonQuery();
        }

        var line = 0;
        for (var n = 1; n <= symbols; n++)
        {
            for (var hit = 0; hit < n; hit++)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char) "
                  + "VALUES (1, $s, 0, $l, 0)";
                cmd.Parameters.AddWithValue("$s", $"App.N{n:D2}.Thing");
                cmd.Parameters.AddWithValue("$l", line++);
                cmd.ExecuteNonQuery();
            }
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    /// <summary>
    /// The schema exactly as it stood before document.generated was added, unstamped,
    /// which is what a developer's cache from that build still holds.
    /// </summary>
    private static void WritePreviousSchemaIndexFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE document (
                    id            INTEGER PRIMARY KEY,
                    relative_path TEXT NOT NULL UNIQUE,
                    language      TEXT NOT NULL
                );
                CREATE TABLE occurrence (
                    id            INTEGER PRIMARY KEY,
                    document_id   INTEGER NOT NULL REFERENCES document(id),
                    symbol        TEXT NOT NULL,
                    is_definition INTEGER NOT NULL,
                    start_line    INTEGER NOT NULL,
                    start_char    INTEGER NOT NULL,
                    enc_end_line  INTEGER,
                    enc_end_char  INTEGER
                );
                CREATE VIRTUAL TABLE symbol_fts USING fts5(symbol);
                CREATE TABLE index_health (
                    built_at_utc TEXT NOT NULL,
                    git_ref      TEXT,
                    degraded     INTEGER NOT NULL,
                    detail       TEXT
                );
                INSERT INTO document(id, relative_path, language) VALUES
                    (1, 'App/Models/Perfume.cs', 'csharp');
                INSERT INTO occurrence(document_id, symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char) VALUES
                    (1, 'App.Models.Perfume.Status', 1, 10, 4, 12, 5);
                INSERT INTO symbol_fts(symbol) VALUES ('App.Models.Perfume.Status');
                """;
            cmd.ExecuteNonQuery();
        }

        IndexHealth.Write(db, new HealthRecord(DateTime.UtcNow, null, false, null));
    }

    /// <summary>Restamps an existing index file, leaving its tables alone.</summary>
    private static void SetSchemaVersion(string path, int version)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version}";
        cmd.ExecuteNonQuery();
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
