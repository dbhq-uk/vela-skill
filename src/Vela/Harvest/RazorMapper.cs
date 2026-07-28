using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vela.Harvest;

public record SourceLocation(string FilePath, int Line, int Character);

public static class RazorMapper
{
    /// <summary>
    /// Resolves a position in a syntax tree to the file a developer can open.
    ///
    /// For generated Razor, the tree carries #line directives pointing back at the
    /// originating .cshtml or .razor, and Roslyn resolves them via GetMappedLineSpan.
    /// For ordinary C# the mapped span is the file itself.
    /// </summary>
    public static SourceLocation? MapToOriginal(SyntaxTree tree, int position)
    {
        if (position < 0 || position > tree.Length) return null;

        var mapped = tree.GetMappedLineSpan(new TextSpan(position, 0));
        var path = string.IsNullOrEmpty(mapped.Path) ? tree.FilePath : mapped.Path;
        if (string.IsNullOrEmpty(path)) return null;

        return new SourceLocation(path, mapped.StartLinePosition.Line, mapped.StartLinePosition.Character);
    }
}
