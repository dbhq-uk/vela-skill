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
