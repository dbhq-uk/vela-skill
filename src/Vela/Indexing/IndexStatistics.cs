using System.Text;
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// What one pass over the code put in this index.
/// </summary>
/// <param name="Source">
/// The value in document.source, with its one sentinel intact: "" is vela's own Roslyn
/// harvest, which has no file to point at because it read the compilation rather than a
/// file, and anything else is the absolute path of the .scip an import read. It is kept
/// raw here and named on the way out, so a caller can still tell the two apart.
/// </param>
public record SourceStats(string Source, int Documents, int Occurrences);

/// <summary>
/// What an index actually contains, counted rather than assumed, the one set of files it
/// deliberately does not contain, and where each of its documents came from.
/// </summary>
public record IndexStats(
    int Documents,
    int GeneratedDocuments,
    int RazorDocuments,
    int Occurrences,
    int RazorOccurrences,
    int Definitions,
    IReadOnlyList<SourceStats> Sources,
    IReadOnlyList<string> ExternalDocuments);

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
        Definitions: Count(db, "SELECT COUNT(*) FROM occurrence WHERE is_definition = 1"),
        Sources: ReadSources(db),
        ExternalDocuments: Vela.Indexing.ExternalDocuments.Read(db));

    /// <summary>
    /// What each pass contributed, in the order '' sorts first, which puts vela's own
    /// harvest above the imports that were added beside it.
    ///
    /// A LEFT JOIN and COUNT(DISTINCT d.id), not a pair of grouped counts: a document
    /// carrying no occurrences at all still came from somewhere and still has to appear,
    /// or the breakdown stops adding up to the total it is a breakdown of. That is a real
    /// row rather than a hypothetical one - an imported .scip can name a file it found
    /// nothing in - and a source whose documents were all empty would otherwise vanish
    /// from the one report that exists to say what is in the index.
    /// </summary>
    private static IReadOnlyList<SourceStats> ReadSources(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT d.source, COUNT(DISTINCT d.id), COUNT(o.id)
            FROM document d
            LEFT JOIN occurrence o ON o.document_id = d.id
            GROUP BY d.source
            ORDER BY d.source
            """;

        var sources = new List<SourceStats>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sources.Add(new SourceStats(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));

        return sources;
    }

    public static string Render(IndexStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"documents            : {stats.Documents}");
        sb.AppendLine($"  generated          : {stats.GeneratedDocuments}   (compiled, not on disk)");
        sb.AppendLine($"  razor views        : {stats.RazorDocuments}   (.cshtml and .razor)");
        sb.AppendLine($"occurrences          : {stats.Occurrences}");
        sb.AppendLine($"  in razor views     : {stats.RazorOccurrences}");
        sb.AppendLine($"  definitions        : {stats.Definitions}");

        // WHERE EACH DOCUMENT CAME FROM, which --stats could not say at all. An index
        // holding C# vela harvested and TypeScript somebody's scip-typescript run
        // produced reported one undifferentiated pile, so a user with a polyglot index
        // could not see what came from where - which is exactly the question this option
        // exists to answer, and the answer has been sitting in document.source since
        // schema 7.
        //
        // Printed even when there is only one, because "nothing has been imported into
        // this index" is a fact somebody may have come here to check, and a block that
        // appears only sometimes is one nobody learns to look for.
        //
        // Every .scip is NAMED rather than numbered. A per-source count that would not
        // say which source is the same pile with more numbers in it, and the path is what
        // a reader needs to re-run the indexer or to run vela import again.
        sb.AppendLine($"sources              : {stats.Sources.Count}   (where each document came from)");
        foreach (var source in stats.Sources)
        {
            var origin = source.Source.Length == 0 ? "roslyn harvest" : "imported .scip";
            var name = source.Source.Length == 0 ? "" : "   " + source.Source;
            // Padded to the same column as every other label above, so the block reads as
            // part of the report rather than as something bolted to the end of it.
            sb.AppendLine($"  {origin,-19}: {source.Documents} document(s), "
                        + $"{source.Occurrences} occurrence(s)" + name);
        }

        // Named, not just counted. This is the only place the skipped paths can be
        // seen, and --stats is where somebody has already asked what is in the index,
        // so the whole list is printed rather than a sample: a list with an unexplained
        // tail is the problem this is fixing. The set is small by construction, because
        // only a package or the SDK can put a file in it.
        if (stats.ExternalDocuments.Count > 0)
        {
            sb.AppendLine($"external documents   : {stats.ExternalDocuments.Count}   "
                        + "(not indexed: from a NuGet package, the .NET SDK, or outside this tree)");
            foreach (var path in stats.ExternalDocuments) sb.AppendLine("  " + path);
        }

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
