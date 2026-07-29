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
    /// Why find came back empty.
    ///
    /// The other four verbs say which absence an empty answer is; find printed the list
    /// and stopped, which under Constraint 3 is the most dangerous shape of all, because
    /// find is the verb an agent reaches for before deciding a name does not exist in
    /// the codebase. Two absences print the same "0 symbol(s)": an index with nothing in
    /// it, and an index that simply holds nothing of that name.
    ///
    /// It also names the one place find and the other verbs genuinely differ. find
    /// matches FTS5 tokens and a trailing prefix, so "Stat" finds Status; refs, def and
    /// impact match a whole dotted segment exactly, so "Stat" finds nothing. A caller who
    /// gets an answer from one and not the other needs to know that is the reason.
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string pattern)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbol_fts";
        var indexed = Convert.ToInt64(cmd.ExecuteScalar());

        if (indexed == 0)
            return "This index contains no symbols at all, so the empty answer is about the index and "
                 + "says nothing whatever about the code. Run vela index, and check the build succeeded.";

        return $"No symbol name in the index matches '{pattern}'. That is a statement about the index, "
             + "not about the code: this empty result is not evidence that no such symbol exists. find "
             + "matches whole name tokens and a prefix of the last one, so 'Stat' finds 'Status' but "
             + "'tatus' finds nothing. Check the spelling, and check the index covers the project that "
             + "declares it.";
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
