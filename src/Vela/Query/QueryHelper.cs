using System.Text;
using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class QueryHelper
{
    /// <summary>
    /// The characters an identifier is made of, and the characters a stored name is
    /// made of outside a bracketed group: an identifier, or several joined by dots.
    /// Written out because SQLite's rtrim and ltrim take a set of characters rather
    /// than a class.
    /// </summary>
    private const string IdentifierCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private const string NameCharacters = IdentifierCharacters + ".";

    /// <summary>The name SQLite knows <see cref="Matches"/> by.</summary>
    private const string MatchFunction = "vela_matches";

    /// <summary>
    /// The one substring a symbol must contain for <see cref="Matches"/> to have any
    /// chance of returning true, computed from the pattern bound as $s.
    ///
    /// It is the pattern's trailing run of identifier characters, and where the pattern
    /// ends in something an identifier cannot contain, such as the ')' of a signature,
    /// its leading run instead. Either way it is one maximal run of identifier
    /// characters of the pattern, and <see cref="Matches"/> cannot succeed without the
    /// symbol containing it: see the proof in <see cref="SymbolMatches"/>.
    ///
    /// SQLite's instr returns 1 for an empty needle, so a pattern with no identifier
    /// character in it at all needs no special case: the condition is simply true, and
    /// the full rule decides.
    /// </summary>
    private static readonly string NecessarySubstring =
        $"""
         CASE WHEN substr($s, length(rtrim($s, '{IdentifierCharacters}')) + 1) <> ''
              THEN substr($s, length(rtrim($s, '{IdentifierCharacters}')) + 1)
              ELSE substr($s, 1, length($s) - length(ltrim($s, '{IdentifierCharacters}')))
         END
         """;

    /// <summary>
    /// The SQL predicate matching a stored symbol name against a user's pattern, for
    /// the symbol column named by <paramref name="symbolColumn"/>. The pattern is
    /// always bound as $s.
    ///
    /// A match is a whole dotted segment, and it is case-sensitive.
    ///
    /// Both halves of that sentence were wrong before, and SQLite will confirm it:
    /// 'App.Models.HttpStatus' LIKE '%Status' is 1, 'Guid' LIKE '%Id' is 1, and
    /// 'App.Perfume.Name' LIKE '%name' is 1, because SQLite's LIKE is unanchored where
    /// you put a '%' and case-insensitive for ASCII whatever you do. So `refs Status`
    /// merged Perfume.Status with HttpStatus and OrderStatus, `def Name` returned
    /// FirstName and LastName as one answer, and `impact Id` attributed every caller
    /// that touched a Guid. Name, Status, Value, Id and Update are precisely the
    /// identifiers vela exists to disambiguate, so this put back the noise the tool is
    /// for, in the place it is least expected.
    ///
    /// LIKE is therefore not used at all. Case sensitivity cannot be recovered from it
    /// (COLLATE does not reach the like() function; only the process-wide
    /// case_sensitive_like pragma does), and the comparisons the rule makes are ordinal
    /// string equality, which is exactly the comparison .NET identifiers need. Dropping
    /// LIKE also removes the wildcard-escaping problem at the source rather than
    /// papering over it: '%' and '_' in a pattern are ordinary characters because there
    /// is no pattern language left for them to mean anything in.
    ///
    /// <b>Why the rule itself is not written in SQL.</b> It used to be, twice: once
    /// here and once in C# for the ambiguity block, from a shared character-set
    /// constant so the two could not drift. That worked while the only thing to remove
    /// from a name was a parameter list, which is one cut. Removing generic type
    /// argument lists is not: they nest, they contain dots, commas and a tuple's
    /// parentheses, and there can be several of them in one name, so reading them means
    /// matching brackets. SQLite's expression language cannot do that at all, and the
    /// recursive CTE that could would run per character over every symbol in the index.
    /// So the rule is <see cref="Matches"/>, in C#, and SQLite calls it as a
    /// deterministic user-defined function registered by <see cref="Register"/>. There
    /// is now one implementation rather than two that agree, which is a stronger
    /// guarantee than the one it replaces, and no third copy of it exists anywhere.
    ///
    /// <b>What the predicate still does in SQL.</b> A function call per row would be
    /// paid on all 935,029 occurrences of the real index, so the scan leads with a
    /// necessary condition SQLite answers with a substring search, and the function is
    /// only reached by the rows that pass it. <see cref="NecessarySubstring"/> is one
    /// maximal run of identifier characters of the pattern, and it is necessary because
    /// every name <see cref="Matches"/> reads is the stored name with whole bracketed
    /// groups taken out of it, so each name is a concatenation of pieces of the symbol.
    /// The pattern always begins at a segment boundary of one of those names and always
    /// ends where the name ends, so the run in question is a maximal run of identifier
    /// characters of that name; and no maximal run can straddle two pieces, because a
    /// group is only removed when the character after its closing bracket cannot
    /// continue an identifier (see <see cref="WithoutTypeArguments"/> and
    /// <see cref="DottedName"/>). The run therefore lies inside a single piece of the
    /// symbol, and a piece of the symbol is a substring of it.
    ///
    /// That condition is what makes this a fast scan rather than a slow one. No index
    /// can serve the rule, but no index could serve a leading-'%' LIKE either, so
    /// nothing is lost by comparison with what vela has always done here.
    /// </summary>
    public static string SymbolMatches(string symbolColumn) =>
        $"(instr({symbolColumn}, {NecessarySubstring}) > 0 AND {MatchFunction}({symbolColumn}, $s))";

    /// <summary>
    /// The stored name with its generic type argument lists removed, so that a symbol
    /// is reachable by name whatever it was constructed with.
    ///
    /// This is the other half of "a name is what you can see in the source". A
    /// constructed generic carries its arguments inside its own last dotted segment:
    ///
    ///   Microsoft.Extensions.Logging.ILogger&lt;ScentVerdict.Web.Pages.IndexModel&gt;
    ///   ScentVerdict.Ai.Auditing.AuditRunner.RunWithAuditAsync&lt;(int, int)&gt;(System.String)
    ///
    /// so no bare name reached either of them. On the real solution `refs ILogger`
    /// answered 2 occurrences where 271 exist, and `refs RunWithAuditAsync` matched
    /// none of its 33 instantiations while reporting a confident total, which is the
    /// silent under-count this tool exists to prevent: an answer that says a symbol
    /// used everywhere is barely used is the one that gets it deleted. 13,366 of the
    /// index's 135,321 distinct symbols carry a '&lt;' before any parameter list.
    ///
    /// A type argument list can hold anything a type can, so it is read by matching
    /// brackets rather than by cutting at the first '&lt;': the parentheses of a tuple
    /// argument are counted along with the angle brackets, and the whole group goes,
    /// however deeply it nests and however many of them there are.
    ///
    /// Two guards, and both are load-bearing:
    ///
    /// A name whose brackets do not pair is not a name this rule can read, and it is
    /// returned exactly as it is. 'System.Guid.operator &gt;(System.Guid, System.Guid)'
    /// is that name: 39 symbols in the real index have an angle bracket that opens or
    /// closes nothing. Counting without pairing swallows the rest of the name, and for
    /// 'operator &gt;' the count goes negative and the parameter list is promoted into
    /// the name, so `refs Guid` would answer with an operator as though the type in its
    /// signature were the symbol.
    ///
    /// A group is only removed when what follows its closing bracket cannot continue an
    /// identifier. Nothing else could sensibly follow a type argument list, and
    /// requiring it is what makes the substring condition in <see cref="SymbolMatches"/>
    /// provably necessary rather than merely true in practice: it is the guarantee that
    /// taking a group out never joins two identifiers into one.
    /// </summary>
    public static string WithoutTypeArguments(string symbol)
    {
        if (symbol.IndexOf('<', StringComparison.Ordinal) < 0) return symbol;

        var kept = new StringBuilder(symbol.Length);
        var closers = new Stack<char>();
        var inTypeArguments = 0;

        for (var i = 0; i < symbol.Length; i++)
        {
            var c = symbol[i];

            if (c is '<' or '(')
            {
                closers.Push(c == '<' ? '>' : ')');
                if (c == '<') inTypeArguments++;
                else if (inTypeArguments == 0) kept.Append(c);
                continue;
            }

            if (c is '>' or ')')
            {
                if (closers.Count == 0 || closers.Pop() != c) return symbol;

                if (c == ')')
                {
                    if (inTypeArguments == 0) kept.Append(c);
                    continue;
                }

                if (--inTypeArguments > 0) continue;

                // What follows a type argument list is a dot, a parameter list or the
                // end of the name. An identifier character there means this is not one.
                if (i + 1 < symbol.Length
                    && IdentifierCharacters.Contains(symbol[i + 1], StringComparison.Ordinal))
                    return symbol;

                continue;
            }

            if (inTypeArguments == 0) kept.Append(c);
        }

        return closers.Count == 0 ? kept.ToString() : symbol;
    }

    /// <summary>
    /// The stored name with its parameter list removed.
    ///
    /// Removing the parameter list means removing the parameter list and nothing else.
    /// Cutting the name at the first '(' also threw away every segment after the
    /// closing ')', and a local or a parameter is stored as exactly that:
    /// 'App.Services.PerfumeService.PerfumeService(ILogger&lt;...&gt;, IImageService).logger'.
    /// Cut at the first '(' it read '...PerfumeService.PerfumeService', whose last
    /// segment is PerfumeService, so `refs PerfumeService` answered with the
    /// constructor's parameters as though they were the type, and `refs Get` answered
    /// 9,493 occurrences on the real solution where 362 are real: the other 9,131 were
    /// locals and parameters declared inside some Get(...). That is the noise vela
    /// exists to remove, reintroduced by the one place that shortens a name.
    ///
    /// So the parameter list ends at the last ')' that a run of name characters
    /// follows, and that run is kept. A parameter is then reachable by its own name,
    /// and not by the name of the type or method it is declared in.
    ///
    /// A '(' at position 0 opens a tuple type rather than a parameter list, and a
    /// parameter list that is not followed by a run of name characters back to its ')'
    /// is not one either, so both are returned untouched. The run that is kept begins
    /// with a dot for the same reason the guard in <see cref="WithoutTypeArguments"/>
    /// exists, and the substring condition in <see cref="SymbolMatches"/> relies on it.
    /// </summary>
    public static string DottedName(string symbol)
    {
        var open = symbol.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0) return symbol;

        var name = symbol.Length;
        while (name > 0 && NameCharacters.Contains(symbol[name - 1], StringComparison.Ordinal)) name--;

        return name > 0 && symbol[name - 1] == ')' && (name == symbol.Length || symbol[name] == '.')
            ? symbol[..open] + symbol[name..]
            : symbol;
    }

    /// <summary>
    /// The stored name with both its type arguments and its parameter list removed:
    /// the dotted segments a reader can see in the source, and the form the ambiguity
    /// block lengthens a pattern from.
    /// </summary>
    public static string PlainName(string symbol) => DottedName(WithoutTypeArguments(symbol));

    /// <summary>
    /// Whether a stored symbol name matches a pattern. This is the rule: the SQL
    /// predicate in <see cref="SymbolMatches"/> calls this very function, and the
    /// ambiguity block calls it directly, so there is nothing for the two to disagree
    /// about.
    ///
    /// A pattern matches when it is the whole of, or a trailing run of the dotted
    /// segments of, any of four readings of the stored name: the name as it is, the
    /// name without its parameter list, the name without its type arguments, and the
    /// name without either. Every one of them is needed. 'Publish' and
    /// 'App.Services.PerfumeService.Publish(App.Models.Perfume)' both have to reach the
    /// method, and so does 'Publish(App.Models.Perfume)' when the stored name is
    /// 'Publish&lt;T&gt;(App.Models.Perfume)': a signature the reader typed out of the
    /// answer must still find what the answer was about.
    ///
    /// Comparison is ordinal throughout, so Name and name stay two different members,
    /// and a match always begins at a segment boundary, so 'Status' does not match
    /// 'HttpStatus'.
    /// </summary>
    public static bool Matches(string symbol, string pattern)
    {
        if (EndsWithSegments(symbol, pattern) || EndsWithSegments(DottedName(symbol), pattern)) return true;

        var name = WithoutTypeArguments(symbol);

        return !string.Equals(name, symbol, StringComparison.Ordinal)
            && (EndsWithSegments(name, pattern) || EndsWithSegments(DottedName(name), pattern));
    }

    private static bool EndsWithSegments(string name, string pattern) =>
        string.Equals(name, pattern, StringComparison.Ordinal)
        || (name.Length > pattern.Length
            && name[name.Length - pattern.Length - 1] == '.'
            && name.EndsWith(pattern, StringComparison.Ordinal));

    /// <summary>
    /// Teaches a connection the one word of SQL that is not SQL. Called by every helper
    /// below that runs a query, rather than at the point a connection is opened, so a
    /// caller cannot construct a connection that answers a symbol query wrongly: the
    /// alternative is a missing-function error at the worst possible moment, which is
    /// exactly the kind of silence this project spends its comments on.
    ///
    /// Deterministic, because it is: the same symbol and pattern give the same answer on
    /// every machine and every run (Constraint 1).
    /// </summary>
    private static void Register(SqliteConnection db) =>
        db.CreateFunction(
            MatchFunction,
            (string? symbol, string? pattern) =>
                symbol is not null && pattern is not null && Matches(symbol, pattern),
            isDeterministic: true);

    /// <summary>
    /// A command over this connection, with the pattern bound as $s and the matching
    /// rule in place. Every query vela runs is built here, which is what makes
    /// <see cref="Register"/> unmissable.
    /// </summary>
    private static SqliteCommand Command(SqliteConnection db, string sql, string parameter)
    {
        Register(db);

        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", parameter);
        return cmd;
    }

    /// <summary>
    /// Runs a hit query whose $s is an exact value, for example a document path.
    /// </summary>
    public static IReadOnlyList<Hit> Select(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = Command(db, sql, parameter);

        var hits = new List<Hit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            hits.Add(new Hit(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                             reader.GetString(3), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0));
        return hits;
    }

    /// <summary>
    /// Runs a query returning one row per symbol with a count beside it, and orders the
    /// result here rather than in SQL. The ordering has to be total and identical on
    /// every machine (Constraint 1), and an ORDER BY that leaves ties unbroken is settled
    /// by whatever the query plan produced.
    /// </summary>
    public static IReadOnlyList<SymbolTally> Tally(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = Command(db, sql, parameter);

        var tallies = new List<SymbolTally>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tallies.Add(new SymbolTally(reader.GetString(0), reader.GetInt32(1)));

        return Ambiguity.Ordered(tallies);
    }

    /// <summary>Runs a query whose single row and column is a count.</summary>
    public static int Count(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = Command(db, sql, parameter);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// True when the index holds any occurrence of a symbol matching the pattern,
    /// definitions included. Used to tell "there is nothing to report" apart from
    /// "this symbol was never indexed", which print identically otherwise.
    /// </summary>
    public static bool AnySymbolOccurrence(SqliteConnection db, string symbolPattern)
        => Exists(db, $"""
            SELECT 1 FROM occurrence
            WHERE {SymbolMatches("symbol")}
            LIMIT 1
            """, symbolPattern);

    /// <summary>
    /// True when the index holds an occurrence of the symbol in a document that is on
    /// disk. Its negative is the case this exists for: a symbol vela indexed perfectly
    /// well whose every occurrence sits in the Razor generator's output, so that refs
    /// and impact suppress the lot and answer "0 result(s)".
    /// </summary>
    public static bool AnySymbolOccurrenceOnDisk(SqliteConnection db, string symbolPattern)
        => Exists(db, $"""
            SELECT 1
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE d.generated = 0
              AND {SymbolMatches("o.symbol")}
            LIMIT 1
            """, symbolPattern);

    /// <summary>True when the index holds a non-definition occurrence of the symbol.</summary>
    public static bool AnySymbolReference(SqliteConnection db, string symbolPattern)
        => Exists(db, $"""
            SELECT 1 FROM occurrence
            WHERE is_definition = 0
              AND {SymbolMatches("symbol")}
            LIMIT 1
            """, symbolPattern);

    /// <summary>True when the index holds a document at exactly this relative path.</summary>
    public static bool DocumentExists(SqliteConnection db, string relativePath)
        => Exists(db, "SELECT 1 FROM document WHERE relative_path = $s LIMIT 1", relativePath);

    /// <summary>
    /// The one wording for "vela has never heard of this symbol", shared by every
    /// verb that takes a symbol so the three cannot drift apart. It says outright
    /// which question the empty answer is answering, because an agent handed a bare
    /// "0 result(s)" concludes the symbol is unused and deletes it.
    /// </summary>
    public static string NoSuchSymbol(string symbolPattern) =>
        $"No symbol matching '{symbolPattern}' is in the index. That is a statement about the index, "
        + "not about the code: nothing of that name was indexed, so this empty result is not evidence "
        + "that the symbol is unused. Names are matched a whole dotted segment at a time, and matching "
        + "is case-sensitive, so 'Status' does not match 'HttpStatus' and 'status' does not match "
        + "'Status'. Check the spelling, and check the index covers the project that declares it.";

    /// <summary>
    /// The one wording for "vela knows this symbol, and everything it knows about it
    /// is in generated code", shared by refs and impact for the same reason
    /// <see cref="NoSuchSymbol"/> is shared.
    ///
    /// This is the sentence that used to be missing, and its absence produced a
    /// self-contradicting answer: "nothing of that name was indexed" printed directly
    /// above "3 further result(s) in generated code". Both cannot be true, and the one
    /// an agent acts on is the first, so it would conclude a Razor page member does not
    /// exist and delete the code that uses it. Saying which absence this is costs one
    /// query on a path that has nothing to report anyway (Constraint 3).
    /// </summary>
    public static string OnlyInGeneratedCode(string symbolPattern) =>
        $"'{symbolPattern}' is in the index: something of that name was indexed, so this is not a "
        + "statement that the symbol does not exist. Every occurrence of it that vela recorded is in "
        + "source-generated code, which the compiler builds from Razor and never writes to disk, and "
        + "which refs and impact leave out of their default answer because the paths cannot be opened. "
        + "This empty result is therefore not evidence that the symbol is unused. Pass "
        + "--include-generated to see those occurrences.";

    private static bool Exists(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = Command(db, sql, parameter);

        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }
}
