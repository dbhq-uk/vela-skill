using Microsoft.Data.Sqlite;

namespace Vela.Query;

internal static class QueryHelper
{
    /// <summary>
    /// The ESCAPE character used by every LIKE in this namespace. It must be spelled
    /// the same way in the SQL text and in <see cref="EscapeLike"/>, so both read it
    /// from here. Backslash cannot appear in a .NET symbol name, so escaping it costs
    /// nothing in practice, and it is escaped anyway for correctness.
    /// </summary>
    public const char LikeEscape = '\\';

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
                             reader.GetString(3), reader.GetInt32(4) != 0));
        return hits;
    }

    /// <summary>
    /// Runs a hit query whose $s is a symbol suffix pattern matched with LIKE. The
    /// wildcards belong to Vela's own SQL, never to the user's input, so the input
    /// is escaped on the way in.
    /// </summary>
    public static IReadOnlyList<Hit> SelectBySymbolSuffix(SqliteConnection db, string sql, string symbolPattern)
        => Select(db, sql, EscapeLike(symbolPattern));

    /// <summary>
    /// True when the index holds any occurrence of a symbol matching the pattern,
    /// definitions included. Used to tell "there is nothing to report" apart from
    /// "this symbol was never indexed", which print identically otherwise.
    /// </summary>
    public static bool AnySymbolOccurrence(SqliteConnection db, string symbolPattern)
        => Exists(db, """
            SELECT 1 FROM occurrence
            WHERE symbol LIKE '%' || $s ESCAPE '\' OR symbol LIKE '%' || $s || '(%' ESCAPE '\'
            LIMIT 1
            """, EscapeLike(symbolPattern));

    /// <summary>True when the index holds a non-definition occurrence of the symbol.</summary>
    public static bool AnySymbolReference(SqliteConnection db, string symbolPattern)
        => Exists(db, """
            SELECT 1 FROM occurrence
            WHERE is_definition = 0
              AND (symbol LIKE '%' || $s ESCAPE '\' OR symbol LIKE '%' || $s || '(%' ESCAPE '\')
            LIMIT 1
            """, EscapeLike(symbolPattern));

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
        + "that the symbol is unused. Check the spelling, and check the index covers the project that declares it.";

    private static bool Exists(SqliteConnection db, string sql, string parameter)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", parameter);

        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    /// <summary>
    /// Escapes the two characters SQL LIKE treats as wildcards, so a search for
    /// Foo_Bar cannot also answer for FooXBar. Underscores are ordinary in .NET
    /// identifiers, and an unasked-for extra hit is indistinguishable from a real
    /// one once it reaches the caller.
    /// </summary>
    public static string EscapeLike(string value) => value
        .Replace($"{LikeEscape}", $"{LikeEscape}{LikeEscape}", StringComparison.Ordinal)
        .Replace("%", $"{LikeEscape}%", StringComparison.Ordinal)
        .Replace("_", $"{LikeEscape}_", StringComparison.Ordinal);
}
