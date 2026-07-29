using System.Text;
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// What an index actually contains, counted rather than assumed.
/// </summary>
public record IndexStats(
    int Documents,
    int GeneratedDocuments,
    int RazorDocuments,
    int Occurrences,
    int RazorOccurrences,
    int Definitions);

/// <summary>
/// The numbers behind `vela index --stats`.
///
/// This exists because the one property that must never regress is invisible when it
/// does. Razor views and Blazor components reach the compiler as source-generated
/// documents, and a change that stops enumerating them does not fail: the index still
/// builds, every query still answers, and the Razor half of the codebase is simply not
/// in it. There is no error to see. A count is the only thing that shows it.
/// </summary>
public static class IndexStatistics
{
    public static IndexStats Read(SqliteConnection db) => new(
        Documents: Count(db, "SELECT COUNT(*) FROM document"),
        GeneratedDocuments: Count(db, "SELECT COUNT(*) FROM document WHERE generated = 1"),
        RazorDocuments: Count(db, "SELECT COUNT(*) FROM document WHERE language = 'razor'"),
        Occurrences: Count(db, "SELECT COUNT(*) FROM occurrence"),
        RazorOccurrences: Count(db, """
            SELECT COUNT(*) FROM occurrence o
            JOIN document d ON d.id = o.document_id
            WHERE d.language = 'razor'
            """),
        Definitions: Count(db, "SELECT COUNT(*) FROM occurrence WHERE is_definition = 1"));

    public static string Render(IndexStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"documents            : {stats.Documents}");
        sb.AppendLine($"  generated          : {stats.GeneratedDocuments}   (compiled, not on disk)");
        sb.AppendLine($"  razor views        : {stats.RazorDocuments}   (.cshtml and .razor)");
        sb.AppendLine($"occurrences          : {stats.Occurrences}");
        sb.AppendLine($"  in razor views     : {stats.RazorOccurrences}");
        sb.AppendLine($"  definitions        : {stats.Definitions}");

        // A zero here is the silent regression this whole option exists to catch, so it
        // is called out rather than left as a number among numbers.
        if (stats.RazorDocuments == 0)
        {
            sb.AppendLine("No Razor views are indexed. If this solution has .cshtml or .razor files, "
                        + "source-generated documents are not reaching the index.");
        }
        else if (stats.RazorOccurrences == 0)
        {
            sb.AppendLine("Razor views are indexed but carry no occurrences, so nothing in them maps "
                        + "back to a symbol. Positions are not being mapped through #line directives.");
        }

        return sb.ToString();
    }

    private static int Count(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
