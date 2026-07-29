using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// The files an index deliberately does not hold: source contributed from the NuGet
/// package cache or from the .NET installation, which cannot sit under project_root and
/// is nobody's first-party code.
///
/// Stored in the index rather than left in the emitted SCIP index's tool arguments,
/// which are never persisted and never written to disk. `vela index` printed a count and
/// then threw the paths away, so a reader who wanted to check what had been skipped had
/// no way to: the count was a number about nothing they could look at. If the
/// classification is ever wrong about a file, this list is the difference between
/// noticing and not.
///
/// A table rather than the index_health detail. That detail is the text printed in the
/// "!!" banner above every answer from a degraded index, and these files must never
/// degrade anything; it is also summarised past ten entries, so it is lossy by design,
/// which is the one property this list cannot have.
/// </summary>
public static class ExternalDocuments
{
    public static void Write(SqliteConnection db, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        using var tx = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO external_document(path) VALUES ($p)";
        insert.Parameters.Add("$p", SqliteType.Text);

        foreach (var path in paths)
        {
            insert.Parameters["$p"].Value = path;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// The skipped paths, ordered by path rather than by the order the emitter happened
    /// to meet them, so the same solution renders the same list on every run and every
    /// machine (Constraint 1).
    /// </summary>
    public static IReadOnlyList<string> Read(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path FROM external_document ORDER BY path";
        using var reader = cmd.ExecuteReader();

        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }
}
