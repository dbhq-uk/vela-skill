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
        cmd.Parameters.AddWithValue("$p", AsPrefixPhrase(pattern));

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>
    /// Wraps the user's text in an FTS5 phrase, followed by a bare '*', so it is
    /// searched for as text rather than executed as a query, and still matches on a
    /// partial name.
    ///
    /// MATCH takes a query language of its own, in which AND, OR, NOT, NEAR, ':',
    /// '*', '(' and '"' are operators. A symbol name is full of those characters:
    /// Perfume.Status is a syntax error, Status( is a syntax error, and a symbol
    /// called NOT would run an operator instead of searching. Quoting turns the
    /// whole input into one phrase of consecutive tokens, which is what a user
    /// typing a symbol name means, and removes the class of inputs that throw. A
    /// double quote inside the input is doubled, which is how FTS5 escapes it
    /// within a phrase.
    /// </summary>
    /// <remarks>
    /// The trailing '*' is FTS5's prefix operator applied to the phrase, and it is
    /// what makes find usable as the discovery verb: without it `vela find Statu`
    /// silently answers nothing, which for the verb people reach for when they only
    /// know part of a name is the same shape of failure as an index missing the
    /// code. It reopens no syntax, because the '*' is Vela's own character and not
    /// the user's: the input stays inside the quotes, and every character of it is
    /// still text. This stays exact rather than fuzzy (Constraint 1): it matches a
    /// prefix of the last token, so "Stat" finds Status and "tatus" finds nothing.
    /// </remarks>
    private static string AsPrefixPhrase(string pattern)
        => "\"" + pattern.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" *";
}
