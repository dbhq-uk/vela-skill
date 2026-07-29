using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class QueryHelper
{
    /// <summary>
    /// The characters a stored name is made of outside a parameter list: an
    /// identifier, or several joined by dots. Written out because SQLite's rtrim
    /// takes a set of characters rather than a class, and because the SQL below and
    /// <see cref="DottedName"/> have to agree on it exactly or the block that
    /// suggests a longer pattern would suggest one the query cannot answer.
    /// </summary>
    private const string IdentifierCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private const string NameCharacters = IdentifierCharacters + ".";

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
    /// case_sensitive_like pragma does), and '=' on TEXT uses SQLite's default BINARY
    /// collation, which is exactly the comparison .NET identifiers need. Dropping LIKE
    /// also removes the wildcard-escaping problem at the source rather than papering
    /// over it: '%' and '_' in a pattern are now ordinary characters because there is
    /// no pattern language left for them to mean anything in.
    ///
    /// Four ways to match, all exact:
    ///   1. the pattern is the whole stored symbol
    ///   2. the stored symbol ends with '.' followed by the pattern, so the match
    ///      begins at a segment boundary and 'Status' cannot match 'HttpStatus'
    ///   3 and 4. the same two tests against the stored symbol with its parameter list
    ///      removed, which is what lets 'PerfumeService.Publish' still match
    ///      'App.Services.PerfumeService.Publish(App.Models.Perfume)'
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
    /// and not by the name of the type or method it is declared in. The shape has to
    /// be exactly "(parameters) followed by a name" for anything to be removed, so a
    /// generic type argument that happens to contain parentheses, such as
    /// 'RazorPage&lt;(System.String Slug, System.Int32 Count)&gt;', is left alone rather
    /// than mangled.
    ///
    /// No index can serve this, but no index could serve a leading-'%' LIKE either, so
    /// nothing is lost: both are a scan of the symbol column. The scan leads with a
    /// necessary condition SQLite can answer with a substring search, because the
    /// exact tests below are several substr calls and a concatenation each. The
    /// pattern's last segment must appear somewhere in the symbol for any of the four
    /// to hold: the first two compare it against a tail of the stored name, and the
    /// other two against a tail of a name built out of two pieces of it, neither of
    /// which a segment can straddle because the second piece begins with '.'. A
    /// pattern ending in something other than a name character has no last segment,
    /// and then the whole pattern is the necessary condition, which holds for the same
    /// reason. Measured on the real 935,029-occurrence index, this is 2 to 4 times
    /// faster than the predicate it replaces rather than slower.
    /// </summary>
    public static string SymbolMatches(string symbolColumn)
    {
        // The stored name with its parameter list removed: what precedes the first
        // '(', followed by the trailing run of name characters. Guarded by the two
        // tests below rather than by a CASE, so a symbol with no parameter list is
        // never rebuilt at all.
        var head =
            $"(substr({symbolColumn}, 1, instr({symbolColumn}, '(') - 1) || " +
            $"substr({symbolColumn}, length(rtrim({symbolColumn}, '{NameCharacters}')) + 1))";

        // The pattern's last dotted segment, which is the pattern itself when it has
        // no dot, and empty when the pattern ends in a parenthesis or any other
        // character an identifier cannot contain.
        var lastSegment = $"substr($s, length(rtrim($s, '{IdentifierCharacters}')) + 1)";

        return $"""
            (instr({symbolColumn}, CASE WHEN {lastSegment} <> '' THEN {lastSegment} ELSE $s END) > 0
             AND ({symbolColumn} = $s
                  OR substr({symbolColumn}, -(length($s) + 1)) = '.' || $s
                  OR (instr({symbolColumn}, '(') > 1
                      AND substr(rtrim({symbolColumn}, '{NameCharacters}'), -1) = ')'
                      AND ({head} = $s
                           OR substr({head}, -(length($s) + 1)) = '.' || $s))))
            """;
    }

    /// <summary>
    /// The stored name with its parameter list removed, in C#: the same rule
    /// <see cref="SymbolMatches"/> applies in SQL, from the same character set, so
    /// the two cannot drift apart.
    ///
    /// A '(' at position 0 opens a tuple type rather than a parameter list, and a
    /// parameter list that is not followed by a run of name characters back to its
    /// ')' is not one either, so both are returned untouched. That is what keeps
    /// 'Microsoft.AspNetCore.Mvc.Razor.RazorPage&lt;(System.String Slug)&gt;' from being
    /// cut down to 'Microsoft.AspNetCore.Mvc.Razor.RazorPage&lt;'.
    /// </summary>
    public static string DottedName(string symbol)
    {
        var open = symbol.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0) return symbol;

        var name = symbol.Length;
        while (name > 0 && NameCharacters.Contains(symbol[name - 1], StringComparison.Ordinal)) name--;

        return name > 0 && symbol[name - 1] == ')' ? symbol[..open] + symbol[name..] : symbol;
    }

    /// <summary>
    /// Whether a stored symbol name matches a pattern, in C#.
    ///
    /// The query is answered by <see cref="SymbolMatches"/> in SQL; this is the same
    /// four tests over the same <see cref="DottedName"/>, and it exists for the one
    /// job of choosing which longer name the ambiguity block should suggest. No result
    /// set is decided by it, so the two cannot disagree about which rows come back,
    /// and a test feeds the suggestion it produces back through the real SQL to keep
    /// the pair honest.
    /// </summary>
    public static bool Matches(string symbol, string pattern)
    {
        var head = DottedName(symbol);
        var suffix = "." + pattern;

        return symbol == pattern
            || symbol.EndsWith(suffix, StringComparison.Ordinal)
            || head == pattern
            || head.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a hit query whose $s is an exact value, for example a document path.
    /// </summary>
    public static IReadOnlyList<Hit> Select(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", parameter);

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
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", parameter);

        var tallies = new List<SymbolTally>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tallies.Add(new SymbolTally(reader.GetString(0), reader.GetInt32(1)));

        return Ambiguity.Ordered(tallies);
    }

    /// <summary>Runs a query whose single row and column is a count.</summary>
    public static int Count(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", parameter);
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
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", parameter);

        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }
}
