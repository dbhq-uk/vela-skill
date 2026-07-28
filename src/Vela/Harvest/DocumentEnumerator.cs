using Microsoft.CodeAnalysis;

namespace Vela.Harvest;

public record HarvestedDocument(string GeneratedPath, SyntaxTree Tree, bool IsGenerated);

public static class DocumentEnumerator
{
    /// <summary>
    /// Yields every document Roslyn compiles, not every file on disk.
    ///
    /// Razor views and Blazor components never exist as files the compiler reads;
    /// the Razor source generator emits them into the compilation. Enumerating
    /// project.Documents therefore misses all of them, which is exactly the bug
    /// that makes every other .NET indexer Razor-blind.
    /// </summary>
    public static async IAsyncEnumerable<HarvestedDocument> EnumerateAsync(
        Project project,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var doc in project.Documents)
        {
            var tree = await doc.GetSyntaxTreeAsync(ct);
            if (tree is not null)
                yield return new HarvestedDocument(doc.FilePath ?? doc.Name, tree, IsGenerated: false);
        }

        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(ct))
        {
            var tree = await generated.GetSyntaxTreeAsync(ct);
            if (tree is not null)
                yield return new HarvestedDocument(generated.HintName, tree, IsGenerated: true);
        }
    }
}
