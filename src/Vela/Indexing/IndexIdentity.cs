using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// Which solution an index is of, recorded in the index itself.
///
/// The file name cannot say. An index is `&lt;SolutionName&gt;-&lt;hash&gt;.db`, where the
/// hash is a SHA-256 of the absolute solution path, and a hash does not go backwards: the
/// name is enough to find an index from a solution and useless for the other direction. So
/// `vela cache` could only ever have shown a reader a list of hashes, and nothing could
/// establish that an index describes a repository that is no longer there - which is the
/// one thing that can be evicted with no risk of surprising anybody.
///
/// The path stored is the one <see cref="RealPath"/> resolved: links followed, and on
/// Windows and macOS the letter case the filesystem actually stores read back. That is the
/// spelling `IndexPaths` hashes and the spelling every other part of vela keys on, so an
/// index found by one verb is the index another verb meant.
/// </summary>
public static class IndexIdentity
{
    /// <summary>
    /// Records the solution, replacing whatever was there. One row, like index_health: an
    /// index is of one solution for as long as it exists, and if it were ever of two
    /// nobody could say which.
    /// </summary>
    public static void Write(SqliteConnection db, string solutionPath)
    {
        // DELETE then INSERT in one transaction, so a concurrent reader never sees the
        // window in between with no row in it.
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM index_identity;
            INSERT INTO index_identity(solution_path) VALUES ($s);
            """;
        cmd.Parameters.AddWithValue("$s", solutionPath);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// The solution this index is of, or null when it does not say: an index built before
    /// this was recorded, or one whose table is not there.
    ///
    /// Null is deliberately not an error and deliberately not an empty string. It means
    /// "nothing here can say", which is a different fact from "the solution has gone", and
    /// only the second is ever a reason to remove anything.
    /// </summary>
    public static string? Read(SqliteConnection db)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT solution_path FROM index_identity LIMIT 1";
            var value = cmd.ExecuteScalar() as string;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (SqliteException)
        {
            // No such table, which is every index built before schema 10.
            return null;
        }
    }
}
