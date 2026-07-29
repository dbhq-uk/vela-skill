using Microsoft.Data.Sqlite;

namespace Vela.Query;

public static class OutlineQuery
{
    /// <summary>
    /// The definitions in one document. Generated documents are outlined like any
    /// other, for the same reason def keeps them: the caller has named a specific
    /// document, so refusing to describe it would answer a question nobody asked.
    /// </summary>
    public static IReadOnlyList<Hit> Run(SqliteConnection db, string relativePath)
        => QueryHelper.Select(db, """
            SELECT d.relative_path, o.start_line, o.start_char, o.symbol, o.is_definition, d.generated
            FROM occurrence o JOIN document d ON d.id = o.document_id
            WHERE d.relative_path = $s AND o.is_definition = 1
            ORDER BY o.start_line
            """, relativePath);

    /// <summary>
    /// Why an outline came back empty. A path typed one directory out prints the
    /// same "0 result(s)" as a file that genuinely defines nothing, and the first
    /// reading tells the caller the file is empty when vela has simply never seen
    /// it (Constraint 3).
    /// </summary>
    public static string ExplainEmpty(SqliteConnection db, string relativePath)
        => QueryHelper.DocumentExists(db, relativePath)
            ? $"'{relativePath}' is in the index and no definitions are recorded in it."
            : $"No document with the path '{relativePath}' is in the index, so this says nothing about "
              + "that file's symbols. Paths are matched exactly and are relative to the repository root, "
              + "or to the solution directory when the solution is not in a repository. "
              + "Check the path, and check the index covers this file.";
}
