using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vela.Harvest;

public static class ScipEmitter
{
    public static async Task<Scip.Index> EmitAsync(
        Solution solution, IReadOnlyList<string> failures, CancellationToken ct)
    {
        var projectRoot = Path.GetDirectoryName(solution.FilePath)!;

        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                Version = Scip.ProtocolVersion.UnspecifiedProtocolVersion,
                ToolInfo = new Scip.ToolInfo { Name = "vela", Version = ThisAssemblyVersion() },
                ProjectRoot = new Uri(projectRoot).AbsoluteUri,
                // Metadata.text_document_encoding describes the bytes of the source
                // files on disk, not the position offsets. .NET source, and everything
                // the Razor generator emits, is UTF-8.
                TextDocumentEncoding = Scip.TextEncoding.Utf8
            }
        };

        // Documents are keyed by the file a developer can open, so every generated
        // Razor occurrence folds into its originating .cshtml or .razor document.
        // A null value memoises a path that cannot be a document at all, so the
        // omission is recorded once rather than on every occurrence.
        var byOriginalPath = new Dictionary<string, Scip.Document?>(StringComparer.OrdinalIgnoreCase);

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
                SeedSourceDocuments(harvested.Tree, root, byOriginalPath, index, projectRoot);

                foreach (var node in root.DescendantNodes())
                {
                    var declared = model.GetDeclaredSymbol(node, ct);
                    var symbol = declared ?? model.GetSymbolInfo(node, ct).Symbol;
                    if (symbol is null) continue;

                    var location = RazorMapper.MapToOriginal(harvested.Tree, node.SpanStart);
                    if (location is null) continue;

                    var doc = GetOrAddDocument(byOriginalPath, index, location.FilePath, projectRoot);
                    if (doc is null) continue;

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
                        if (Encloses(location, enclosing))
                            occurrence.EnclosingRange.AddRange(new[]
                            {
                                location.Line, location.Character, enclosing!.Line, enclosing.Character
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
        SyntaxTree tree, SyntaxNode root, Dictionary<string, Scip.Document?> map,
        Scip.Index index, string projectRoot)
    {
        foreach (var trivia in root.GetLeadingTrivia())
        {
            if (trivia.GetStructure() is not PragmaChecksumDirectiveTriviaSyntax checksum) continue;
            var file = checksum.File.ValueText;
            if (!string.IsNullOrEmpty(file))
                GetOrAddDocument(map, index, file, projectRoot);
        }

        foreach (var mapping in tree.GetLineMappings())
        {
            var path = mapping.MappedSpan.Path;
            if (string.IsNullOrEmpty(path)) continue;
            GetOrAddDocument(map, index, path, projectRoot);
        }
    }

    /// <summary>
    /// True when the end position can honestly close a range opened at the start:
    /// the same file, and not before the start.
    ///
    /// The two positions come from two separate #line lookups, and Razor's #line
    /// regions are not monotonic, so a node can begin inside Index.cshtml and end
    /// inside _ViewImports.cshtml, or end above where it started. Either produces a
    /// four-element range whose halves describe different places. Task 8's impact
    /// verb attributes a caller by testing whether a reference falls between these
    /// two lines, so a mixed range does not degrade the answer, it invents one.
    /// When the pair does not hold together, the enclosing range is omitted: a
    /// definition without a body range is a smaller claim, not a false one.
    /// </summary>
    private static bool Encloses(SourceLocation start, SourceLocation? end)
    {
        if (end is null) return false;
        if (!string.Equals(start.FilePath, end.FilePath, StringComparison.OrdinalIgnoreCase)) return false;
        if (end.Line != start.Line) return end.Line > start.Line;
        return end.Character >= start.Character;
    }

    /// <summary>
    /// Returns the document for a source path, or null when the path cannot be a
    /// SCIP document at all. Null is memoised so the omission is recorded once.
    /// </summary>
    private static Scip.Document? GetOrAddDocument(
        Dictionary<string, Scip.Document?> map, Scip.Index index, string path, string projectRoot)
    {
        if (map.TryGetValue(path, out var existing)) return existing;

        var relative = RelativeWithinRoot(projectRoot, path);
        if (relative is null)
        {
            // Constraint 3: an incomplete index must never look like a complete one,
            // so the file we could not represent is named in the index it is missing
            // from, alongside the load failures.
            index.Metadata.ToolInfo.Arguments.Add("outside-project-root: " + path);
            map[path] = null;
            return null;
        }

        var doc = new Scip.Document
        {
            RelativePath = relative,
            Language = LanguageOf(path),
            // scip.proto: the unspecified encoding must not be used by new indexers.
            // Roslyn's character offsets are UTF-16 code units, as they are for every
            // indexer written on .NET.
            PositionEncoding = Scip.PositionEncoding.Utf16CodeUnitOffsetFromLineStart
        };
        map[path] = doc;
        index.Documents.Add(doc);
        return doc;
    }

    /// <summary>
    /// The SCIP path for a file, or null when the file lies outside the project root.
    ///
    /// scip.proto requires relative_path to use '/' on every platform, and requires
    /// every document to live under Metadata.project_root, so '..' is not available
    /// as an escape hatch. A file genuinely outside the root (a Razor Class Library
    /// restored from the NuGet cache is the realistic case) therefore cannot be a
    /// document of this index, and the caller records the omission rather than
    /// emitting a path the spec forbids.
    /// </summary>
    private static string? RelativeWithinRoot(string projectRoot, string path)
    {
        var relative = Path.GetRelativePath(projectRoot, path);

        // On Windows a different volume yields the original absolute path back.
        if (Path.IsPathRooted(relative)) return null;

        relative = relative.Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) return null;

        return relative;
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
