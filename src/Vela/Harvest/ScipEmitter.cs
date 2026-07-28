using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.Harvest;

public static class ScipEmitter
{
    public static async Task<Scip.Index> EmitAsync(
        Solution solution, IReadOnlyList<string> failures, CancellationToken ct)
    {
        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                Version = Scip.ProtocolVersion.UnspecifiedProtocolVersion,
                ToolInfo = new Scip.ToolInfo { Name = "vela", Version = ThisAssemblyVersion() },
                ProjectRoot = new Uri(Path.GetDirectoryName(solution.FilePath)!).AbsoluteUri
            }
        };

        // Documents are keyed by the file a developer can open, so every generated
        // Razor occurrence folds into its originating .cshtml or .razor document.
        var byOriginalPath = new Dictionary<string, Scip.Document>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            await foreach (var harvested in DocumentEnumerator.EnumerateAsync(project, ct))
            {
                var model = compilation.GetSemanticModel(harvested.Tree);
                var root = await harvested.Tree.GetRootAsync(ct);

                // Seed a document for every view this tree was generated from, before
                // looking at any symbol. A view that binds no symbols at all
                // (_ValidationScriptsPartial.cshtml is pure markup) must still appear,
                // as an empty document: an empty document is honest, a missing one
                // says the view does not exist.
                SeedSourceDocuments(harvested.Tree, root, byOriginalPath, index, solution);

                foreach (var node in root.DescendantNodes())
                {
                    var declared = model.GetDeclaredSymbol(node, ct);
                    var symbol = declared ?? model.GetSymbolInfo(node, ct).Symbol;
                    if (symbol is null) continue;

                    var location = RazorMapper.MapToOriginal(harvested.Tree, node.SpanStart);
                    if (location is null) continue;

                    var doc = GetOrAddDocument(byOriginalPath, index, location.FilePath, solution);
                    var isDefinition = declared is not null;

                    var occurrence = new Scip.Occurrence
                    {
                        Symbol = SymbolIdentity.For(symbol),
                        SymbolRoles = isDefinition ? (int)Scip.SymbolRole.Definition : 0
                    };
                    occurrence.Range.AddRange(new[] { location.Line, location.Character, location.Character });

                    if (isDefinition)
                    {
                        var enclosing = RazorMapper.MapToOriginal(harvested.Tree, node.Span.End);
                        if (enclosing is not null)
                            occurrence.EnclosingRange.AddRange(new[]
                            {
                                location.Line, location.Character, enclosing.Line, enclosing.Character
                            });
                    }

                    doc.Occurrences.Add(occurrence);
                }
            }
        }

        // Constraint 3: an incomplete index must never look like a complete one,
        // so the load failures travel with the index they were harvested under.
        foreach (var failure in failures)
            index.Metadata.ToolInfo.Arguments.Add("load-failure: " + failure);

        return index;
    }

    /// <summary>
    /// Creates a document for every distinct source file this tree was generated from:
    /// the targets of its #line directives, plus the #pragma checksum the Razor
    /// generator emits naming the view itself. The checksum is what covers a view with
    /// no C# in it at all, which produces no #line mappings of its own.
    /// For ordinary C# there is neither, and the file gets its document from its first
    /// occurrence instead.
    /// </summary>
    private static void SeedSourceDocuments(
        SyntaxTree tree, SyntaxNode root, Dictionary<string, Scip.Document> map,
        Scip.Index index, Solution solution)
    {
        foreach (var trivia in root.GetLeadingTrivia())
        {
            if (trivia.GetStructure() is not PragmaChecksumDirectiveTriviaSyntax checksum) continue;
            var file = checksum.File.ValueText;
            if (!string.IsNullOrEmpty(file))
                GetOrAddDocument(map, index, file, solution);
        }

        foreach (var mapping in tree.GetLineMappings())
        {
            var path = mapping.MappedSpan.Path;
            if (string.IsNullOrEmpty(path)) continue;
            GetOrAddDocument(map, index, path, solution);
        }
    }

    private static Scip.Document GetOrAddDocument(
        Dictionary<string, Scip.Document> map, Scip.Index index, string path, Solution solution)
    {
        if (map.TryGetValue(path, out var existing)) return existing;

        var root = Path.GetDirectoryName(solution.FilePath)!;
        var relative = Path.GetRelativePath(root, path);
        var doc = new Scip.Document { RelativePath = relative, Language = LanguageOf(path) };
        map[path] = doc;
        index.Documents.Add(doc);
        return doc;
    }

    private static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".vb" => "vb",
        ".cshtml" => "razor",
        ".razor" => "razor",
        _ => "unknown"
    };

    private static string ThisAssemblyVersion() =>
        typeof(ScipEmitter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>Stable, cross-project identity for a symbol.</summary>
public static class SymbolIdentity
{
    private static readonly SymbolDisplayFormat Format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType);

    public static string For(ISymbol symbol) => symbol.ToDisplayString(Format);
}
