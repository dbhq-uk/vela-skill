using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class QueryHelper
{
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
    /// No index can serve this, but no index could serve a leading-'%' LIKE either, so
    /// nothing is lost: both are a scan of the symbol column.
    /// </summary>
    public static string SymbolMatches(string symbolColumn)
    {
        // The stored name with any parameter list removed. instr returns 0 when there
        // is no '(' at all, and substr with a length of -1 would then return the empty
        // string, so the no-parameter case is spelled out rather than relied upon.
        var head =
            $"(CASE WHEN instr({symbolColumn}, '(') > 0 " +
            $"THEN substr({symbolColumn}, 1, instr({symbolColumn}, '(') - 1) " +
            $"ELSE {symbolColumn} END)";

        return $"""
            ({symbolColumn} = $s
             OR substr({symbolColumn}, -(length($s) + 1)) = '.' || $s
             OR {head} = $s
             OR substr({head}, -(length($s) + 1)) = '.' || $s)
            """;
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
