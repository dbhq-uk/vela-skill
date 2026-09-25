using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    /// <summary>
    /// The view a generated tree was built from, or null when the tree names none.
    ///
    /// The Razor generator opens every document it emits with a <c>#pragma checksum</c>
    /// naming the .cshtml or .razor it compiled, by absolute path. It is the first
    /// checksum in the file: the imports a view pulls in reach it through #line
    /// directives, never through a checksum of their own. An ordinary .cs file has no
    /// checksum at all, so this answers null for it.
    /// </summary>
    public static string? ViewOf(SyntaxNode root)
    {
        foreach (var trivia in root.GetLeadingTrivia())
        {
            if (trivia.GetStructure() is not PragmaChecksumDirectiveTriviaSyntax checksum) continue;
            var file = checksum.File.ValueText;
            if (!string.IsNullOrEmpty(file)) return file;
        }

        return null;
    }

    /// <summary>
    /// True when a symbol is a Razor component type: a class that implements
    /// <c>Microsoft.AspNetCore.Components.IComponent</c>, which is what every .razor file
    /// compiles to and what a tag such as <c>&lt;Badge /&gt;</c> names.
    ///
    /// It matters because the Razor generator records no line for either of those. The
    /// component's own class declaration and every <c>OpenComponent&lt;Badge&gt;</c> it
    /// writes for a tag sit in <c>#line hidden</c> code, so without this they stayed in the
    /// generated document: <c>refs Badge</c> answered nothing by default and <c>def Badge</c>
    /// named a .g.cs file nobody can open. Answered from the semantic model, never from
    /// the name, so a class that happens to be called Badge is not swept in.
    /// </summary>
    public static bool IsComponent(ISymbol symbol) =>
        symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } type
        && type.AllInterfaces.Any(IsComponentInterface);

    private static bool IsComponentInterface(INamedTypeSymbol type) =>
        type.Name == "IComponent"
        && type.ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.Components";
}
