using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class FindQuery
{
    /// <summary>
    /// Symbol search by name, over the FTS5 symbol table.
    /// </summary>
    public static IReadOnlyList<string> Run(SqliteConnection db, string pattern)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT symbol FROM symbol_fts WHERE symbol_fts MATCH $p ORDER BY symbol";
        cmd.Parameters.AddWithValue("$p", AsPhrase(pattern));

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>
    /// Wraps the user's text in an FTS5 phrase so it is searched for as text rather
    /// than executed as a query.
    ///
    /// MATCH takes a query language of its own, in which AND, OR, NOT, NEAR, ':',
    /// '*', '(' and '"' are operators. A symbol name is full of those characters:
    /// "Perfume.Status" is a syntax error, "Status(" is a syntax error, and a symbol
    /// called NOT would run an operator instead of searching. Quoting turns the
    /// whole input into one phrase of consecutive tokens, which is what a user
    /// typing a symbol name means, and removes the class of inputs that throw.
    ///
    /// The cost is that FTS5's own operators become unavailable at the command line,
    /// including the trailing '*' prefix search. That is the right trade for a tool
    /// whose contract is exactness: an input that quietly means something other than
    /// itself is worse than one that cannot be expressed. A double quote inside the
    /// input is doubled, which is how FTS5 escapes it within a phrase.
    /// </summary>
    private static string AsPhrase(string pattern)
        => "\"" + pattern.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
