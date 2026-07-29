using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Roslyn covers C# and Visual Basic, and the two syntax namespaces share most of their
// type names, so VB is reached through an alias rather than a second using directive.
using Vb = Microsoft.CodeAnalysis.VisualBasic.Syntax;

using OccurrenceKey = (string Symbol, int Line, int Character, bool IsDefinition);

namespace Vela.Harvest;

/// <summary>
/// An emitted index, plus the two facts about it that SCIP has nowhere to put.
///
/// scip.proto's Document carries a path, a language, occurrences and an encoding, and
/// nothing that says how the document came to exist. vela needs that: a document
/// produced by a source generator, whose occurrences do not map back to a view, names a
/// path that is real to the compiler and absent from the disk, and every verb that
/// reports locations has to know the difference. Extending the proto to carry it would
/// deviate from the format for a consumer-side concern, so it travels beside the index
/// instead and is stored in vela's own schema.
///
/// <paramref name="DisplayNames"/> is the second. Occurrence.symbol is the wire format
/// and holds the SCIP moniker, and vela's own identity for a symbol is a Roslyn display
/// string that every query matches against (see <see cref="SymbolIdentity"/>). The two
/// are not interchangeable and neither can be derived from the other: a moniker names a
/// declaration, so `List&lt;Perfume&gt;` and `List&lt;int&gt;` share one, and a local
/// symbol's moniker is an integer that encodes no name at all. So the display name
/// travels beside the occurrence it belongs to. Keyed by reference, because two
/// protobuf messages holding the same values are equal to each other and are still two
/// different occurrences.
/// </summary>
public record EmitResult(
    Scip.Index Index,
    IReadOnlySet<string> GeneratedDocuments,
    IReadOnlyDictionary<Scip.Occurrence, string>? DisplayNames = null)
{
    /// <summary>
    /// What vela calls this occurrence's symbol, falling back to the SCIP symbol when
    /// nothing said otherwise. The fallback is what an index read from somebody else's
    /// .scip file gets: their symbol is the only name it has.
    /// </summary>
    public string DisplayNameOf(Scip.Occurrence occurrence) =>
        DisplayNames is not null && DisplayNames.TryGetValue(occurrence, out var name)
            ? name
            : occurrence.Symbol;
}

public static class ScipEmitter
{
    public static async Task<EmitResult> EmitAsync(
        Solution solution, IReadOnlyList<string> failures, CancellationToken ct)
    {
        var roots = Roots.Resolve(Path.GetDirectoryName(solution.FilePath)!);

        var index = new Scip.Index
        {
            Metadata = new Scip.Metadata
            {
                Version = Scip.ProtocolVersion.UnspecifiedProtocolVersion,
                ToolInfo = new Scip.ToolInfo { Name = "vela", Version = ThisAssemblyVersion() },
                ProjectRoot = new Uri(roots.ProjectRoot).AbsoluteUri,
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

        // What has already been recorded, per document. The walk below visits every
        // descendant node, and several nodes can carry the same fact: an invocation,
        // the member access it is made through and the identifier that names the
        // method are three nodes and one call site. Each is anchored at the name it
        // spells (see NamingNode), so all three arrive here at one position, and the
        // second and third arrivals are dropped. Emitting them would make refs print
        // the same hit repeatedly and report a count that is simply wrong, so it is
        // stopped at the source rather than hidden by the query. Keyed by relative
        // path, which is unique per document.
        var emitted = new Dictionary<string, Dictionary<OccurrenceKey, Scip.Occurrence>>(StringComparer.Ordinal);

        // Whether every occurrence recorded into a document so far came from a
        // source-generated tree that did not map back to a view. One contribution from
        // an on-disk tree, or one occurrence in a generated tree that a #line directive
        // sends back to its .cshtml, is enough to make the document openable, and an
        // openable document must never be suppressed.
        var generatedOnly = new Dictionary<string, bool>(StringComparer.Ordinal);

        // The two names, side by side. The moniker goes on the occurrence, because that
        // field is the wire format; the display name travels beside it, because that is
        // what every query matches against and it cannot be recovered from the moniker.
        var monikers = new ScipMoniker();
        var displayNames = new Dictionary<Scip.Occurrence, string>(ReferenceEqualityComparer.Instance);

        // Which symbols each document has already described, so SymbolInformation is
        // written once per document however many times the symbol is defined in it.
        var described = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                // Constraint 3: a project with no compilation contributes nothing at
                // all to this index. Skipping it silently, which is what a bare
                // `continue` did, produced an index missing a whole project and a
                // health record that said everything was fine.
                index.Metadata.ToolInfo.Arguments.Add(
                    $"no-compilation: project '{project.Name}' produced no compilation, "
                    + "so none of its code is in this index");
                continue;
            }

            RecordCompilationErrors(index, project, compilation, ct);

            await foreach (var harvested in DocumentEnumerator.EnumerateAsync(project, ct))
            {
                var model = compilation.GetSemanticModel(harvested.Tree);
                var root = await harvested.Tree.GetRootAsync(ct);

                // Seed a document for every view this tree was generated from, before
                // looking at any symbol. A view that binds no symbols at all
                // (_ValidationScriptsPartial.cshtml is pure markup) must still appear,
                // as an empty document: an empty document is honest, a missing one
                // says the view does not exist.
                SeedSourceDocuments(harvested.Tree, root, byOriginalPath, index, roots);

                foreach (var node in root.DescendantNodes())
                {
                    var declared = model.GetDeclaredSymbol(node, ct);
                    var symbol = declared ?? model.GetSymbolInfo(node, ct).Symbol;
                    if (symbol is null) continue;

                    var isDefinition = declared is not null;

                    // Where the occurrence is recorded. A declaration is recorded at the
                    // token that names it (see DeclarationAnchor), a reference at the
                    // name that spells it (see NamingNode). Both rules say the same
                    // thing: an occurrence sits on the identifier, which is where a
                    // reader jumping to it expects to land.
                    var anchorPosition = isDefinition
                        ? DeclarationAnchor(node)
                        : NamingNode(node).SpanStart;

                    var location = RazorMapper.MapToOriginal(harvested.Tree, anchorPosition);
                    if (location is null) continue;

                    var doc = GetOrAddDocument(byOriginalPath, index, location.FilePath, roots);
                    if (doc is null) continue;

                    // Generated, and it stayed generated: the position mapped to the
                    // generator's own output rather than back to a file on disk.
                    var stayedInGeneratedCode = harvested.IsGenerated
                        && string.Equals(location.FilePath, harvested.Tree.FilePath, StringComparison.OrdinalIgnoreCase);

                    generatedOnly[doc.RelativePath] =
                        generatedOnly.TryGetValue(doc.RelativePath, out var soFar)
                            ? soFar && stayedInGeneratedCode
                            : stayedInGeneratedCode;

                    var name = SymbolIdentity.For(symbol);
                    var moniker = monikers.For(symbol, doc.RelativePath);

                    int[]? enclosingRange = null;
                    if (isDefinition && CanEnclose(symbol))
                    {
                        var enclosing = RazorMapper.MapToOriginal(harvested.Tree, node.Span.End);
                        if (Encloses(location, enclosing))
                            enclosingRange = new[]
                            {
                                location.Line, location.Character, enclosing!.Line, enclosing.Character
                            };
                    }

                    if (!emitted.TryGetValue(doc.RelativePath, out var seen))
                        emitted[doc.RelativePath] = seen = new Dictionary<OccurrenceKey, Scip.Occurrence>();

                    var key = new OccurrenceKey(name, location.Line, location.Character, isDefinition);
                    if (seen.TryGetValue(key, out var already))
                    {
                        // The same symbol, at the same position, in the same role: one
                        // fact reached through a second syntax node. Nothing new is
                        // recorded, but an enclosing range only the later node could
                        // map is still an addition rather than a repeat, and dropping
                        // it would quietly shrink what impact can attribute.
                        if (already.EnclosingRange.Count == 0 && enclosingRange is not null)
                            already.EnclosingRange.AddRange(enclosingRange);
                        continue;
                    }

                    var occurrence = new Scip.Occurrence
                    {
                        Symbol = moniker,
                        SymbolRoles = isDefinition ? (int)Scip.SymbolRole.Definition : 0
                    };
                    occurrence.Range.AddRange(new[] { location.Line, location.Character, location.Character });
                    if (enclosingRange is not null) occurrence.EnclosingRange.AddRange(enclosingRange);

                    doc.Occurrences.Add(occurrence);
                    displayNames[occurrence] = name;
                    seen[key] = occurrence;

                    // scip.proto: a Document carries the symbols defined within it, so a
                    // consumer gets the kind and the documentation without having to
                    // re-run a compiler. Definitions only, and once each: a reference is
                    // not this document's to describe.
                    if (!isDefinition) continue;

                    if (!described.TryGetValue(doc.RelativePath, out var alreadyDescribed))
                        described[doc.RelativePath] = alreadyDescribed = new HashSet<string>(StringComparer.Ordinal);

                    if (alreadyDescribed.Add(moniker))
                        doc.Symbols.Add(monikers.Describe(symbol, doc.RelativePath, name));
                }
            }
        }

        // Constraint 3: an incomplete index must never look like a complete one,
        // so the load failures travel with the index they were harvested under.
        foreach (var failure in failures)
            index.Metadata.ToolInfo.Arguments.Add("load-failure: " + failure);

        var generated = generatedOnly
            .Where(entry => entry.Value)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

        return new EmitResult(index, generated, displayNames);
    }

    /// <summary>
    /// Records, into the index, that a project did not compile cleanly.
    ///
    /// This is the quietest way an index can be wrong. Every reference vela stores comes
    /// from a resolved symbol, and a compilation error makes the symbol null for every
    /// node that depends on it, so those references are not skipped loudly, they are
    /// simply absent. One unresolved using directive can remove most of a file's
    /// references while the project still loads, no load failure is raised, and health
    /// reports clean: exactly the shape Constraint 3 exists to forbid, because the
    /// answer that comes back is short rather than obviously broken.
    ///
    /// A count and a small sample, not the whole list: the detail is printed above every
    /// answer from this index, and a wall of compiler output stops being read. Errors are
    /// ordered before sampling so the same solution produces the same sample on every run
    /// and every machine (Constraint 1), rather than whatever order the binder finished in.
    /// </summary>
    private static void RecordCompilationErrors(
        Scip.Index index, Project project, Compilation compilation, CancellationToken ct)
    {
        var errors = compilation.GetDiagnostics(ct)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        if (errors.Count == 0) return;

        var sample = string.Join(" | ", errors
            .Take(CompileErrorSampleSize)
            .Select(d => d.Id + " " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));

        index.Metadata.ToolInfo.Arguments.Add(
            $"compile-error: project '{project.Name}' has {errors.Count} compilation error(s), "
            + $"so references that depend on them are missing from this index. For example: {sample}");
    }

    private const int CompileErrorSampleSize = 3;

    /// <summary>
    /// True when this kind of symbol may carry an enclosing range, which is the same
    /// question as "may this be named as a caller".
    ///
    /// An enclosing range is the body of the thing being defined, and impact reads it
    /// as a containment edge: the innermost definition whose range holds a reference is
    /// reported as the caller. Roslyn's GetDeclaredSymbol succeeds on far more than
    /// members, though. A VariableDeclaratorSyntax declares a local, and the local's
    /// span covers its own initialiser, so `var status = perfume.Status;` produced a
    /// range around the very reference it initialises from, and being the innermost one
    /// it won. `impact Perfume.Status` then answered `status`: a local variable
    /// presented in the same column, in the same shape, as a real calling method.
    ///
    /// Nothing distinguishes the two in the output, so the blast radius stops being
    /// readable as a blast radius. The rule is therefore about what can be a caller,
    /// not about what can be a definition: locals, parameters, range variables, labels
    /// and type parameters are all still indexed and still findable by def and refs;
    /// they simply have no body that other code can sit inside.
    /// </summary>
    private static bool CanEnclose(ISymbol symbol) => symbol.Kind switch
    {
        SymbolKind.Local => false,
        SymbolKind.Parameter => false,
        SymbolKind.RangeVariable => false,
        SymbolKind.Label => false,
        SymbolKind.TypeParameter => false,
        SymbolKind.Discard => false,
        SymbolKind.Alias => false,
        _ => true
    };

    /// <summary>
    /// The position a declaration is recorded at: the token that names it.
    ///
    /// node.SpanStart is the start of the whole declaration, and a declaration begins at
    /// its attribute list. So `def` on
    ///
    ///     [HttpPost]
    ///     public IActionResult Foo()
    ///
    /// answered with the line holding `[HttpPost]`, and with several attributes it
    /// answered several lines above the declaration. That is not wrong in the sense of
    /// naming the wrong member, but it sends the reader to a line that does not look
    /// like what they asked for, and `def` exists to be jumped to.
    ///
    /// The enclosing range opens at this position too, so the body range now begins at
    /// the name rather than at the attributes. That is the more accurate reading in any
    /// case: an attribute argument is not code the member runs.
    /// </summary>
    private static int DeclarationAnchor(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.SpanStart,
        DelegateDeclarationSyntax d => d.Identifier.SpanStart,
        EnumMemberDeclarationSyntax member => member.Identifier.SpanStart,
        MethodDeclarationSyntax method => method.Identifier.SpanStart,
        LocalFunctionStatementSyntax local => local.Identifier.SpanStart,
        ConstructorDeclarationSyntax ctor => ctor.Identifier.SpanStart,
        DestructorDeclarationSyntax dtor => dtor.Identifier.SpanStart,
        // An operator has no identifier; the token a reader looks for is the operator
        // itself, and for a conversion it is the type being converted to.
        OperatorDeclarationSyntax op => op.OperatorToken.SpanStart,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type.SpanStart,
        PropertyDeclarationSyntax property => property.Identifier.SpanStart,
        EventDeclarationSyntax e => e.Identifier.SpanStart,
        IndexerDeclarationSyntax indexer => indexer.ThisKeyword.SpanStart,
        AccessorDeclarationSyntax accessor => accessor.Keyword.SpanStart,
        VariableDeclaratorSyntax variable => variable.Identifier.SpanStart,
        ParameterSyntax parameter => parameter.Identifier.SpanStart,
        TypeParameterSyntax typeParameter => typeParameter.Identifier.SpanStart,
        BaseNamespaceDeclarationSyntax ns => ns.Name.SpanStart,

        // Visual Basic. A VB declaration is two nodes, a block and the statement that
        // opens it, and GetDeclaredSymbol answers on both. They must therefore resolve
        // to the SAME position or the per-document dedup cannot fold them and every VB
        // declaration is recorded twice, so each block defers to its own statement.
        Vb.MethodBlockBaseSyntax block => DeclarationAnchor(block.BlockStatement),
        Vb.TypeBlockSyntax block => DeclarationAnchor(block.BlockStatement),
        Vb.EnumBlockSyntax block => DeclarationAnchor(block.EnumStatement),
        Vb.PropertyBlockSyntax block => DeclarationAnchor(block.PropertyStatement),
        Vb.EventBlockSyntax block => DeclarationAnchor(block.EventStatement),
        Vb.NamespaceBlockSyntax block => DeclarationAnchor(block.NamespaceStatement),

        Vb.TypeStatementSyntax type => type.Identifier.SpanStart,
        Vb.EnumStatementSyntax e => e.Identifier.SpanStart,
        Vb.DelegateStatementSyntax d => d.Identifier.SpanStart,
        Vb.MethodStatementSyntax method => method.Identifier.SpanStart,
        Vb.SubNewStatementSyntax ctor => ctor.NewKeyword.SpanStart,
        Vb.OperatorStatementSyntax op => op.OperatorToken.SpanStart,
        Vb.PropertyStatementSyntax property => property.Identifier.SpanStart,
        Vb.EventStatementSyntax e => e.Identifier.SpanStart,
        Vb.AccessorStatementSyntax accessor => accessor.AccessorKeyword.SpanStart,
        Vb.EnumMemberDeclarationSyntax member => member.Identifier.SpanStart,
        Vb.NamespaceStatementSyntax ns => ns.Name.SpanStart,
        Vb.ParameterSyntax parameter => parameter.Identifier.SpanStart,
        Vb.TypeParameterSyntax typeParameter => typeParameter.Identifier.SpanStart,
        // Locals, fields and parameters all name themselves through this node.
        Vb.ModifiedIdentifierSyntax identifier => identifier.Identifier.SpanStart,

        // Anything either language grows later keeps the position it had. A declaration
        // recorded where it begins is a slightly blunt answer, never a false one.
        _ => node.SpanStart
    };

    /// <summary>
    /// The node that actually names the symbol a reference resolves to.
    ///
    /// The walk visits every descendant node, and one reference is spelled by a chain
    /// of nested nodes that all resolve to the same symbol: `Helper.Do()` is an
    /// invocation, a member access and the identifier `Do`, three nodes and one call.
    /// The invocation and the member access begin at the receiver and the identifier
    /// begins seven characters later, so deduplicating by position cannot see that
    /// they are the same fact, and refs reported two hits per qualified call. Both
    /// locations are real, but the count is not, and the count is the number an agent
    /// uses to size a change.
    ///
    /// The rule, deterministically (Constraint 1): a reference is recorded at the
    /// simple name that spells the symbol. Every node in the chain then resolves to
    /// the same position and the per-document dedup above folds them into one. It also
    /// puts a reference on the identifier itself, which is what a reader is looking
    /// for when they jump to it.
    ///
    /// The descent follows the naming spine only, never a receiver, an argument or an
    /// operand, so two genuinely distinct references stay two: in `Foo(Foo(x))` the
    /// outer invocation names its own callee and the inner invocation is a separate
    /// node with a name of its own. Anything not on the spine is its own anchor, and a
    /// receiver such as `Helper` is a different symbol at a different position and is
    /// recorded in its own right.
    /// </summary>
    private static SyntaxNode NamingNode(SyntaxNode node) => node switch
    {
        // The callee of a call: Helper.Do() -> Helper.Do -> Do.
        InvocationExpressionSyntax invocation => NamingNode(invocation.Expression),
        // The member of a member access, whether or not it is called: perfume.Status
        // resolves to Status on both nodes.
        MemberAccessExpressionSyntax access => access.Name,
        // The member of a conditional access: perfume?.Status.
        MemberBindingExpressionSyntax binding => binding.Name,
        // A dotted type or namespace name: App.Models.Perfume resolves to Perfume on
        // the qualified name and on its right-hand identifier alike.
        QualifiedNameSyntax qualified => qualified.Right,
        // global::App.Models and the like.
        AliasQualifiedNameSyntax alias => alias.Name,

        // Visual Basic, whose syntax kinds mirror the C# ones. Without these the fold
        // never happened in a VB project: the invocation, the member access and the
        // identifier stayed three occurrences of one call, and refs roughly doubled its
        // count for every qualified reference in the codebase.
        Vb.InvocationExpressionSyntax invocation when invocation.Expression is not null
            => NamingNode(invocation.Expression),
        // Covers `x.Member` and, because VB spells the tail of a conditional access as a
        // member access with no left-hand side, `x?.Member` as well.
        Vb.MemberAccessExpressionSyntax access => access.Name,
        Vb.ConditionalAccessExpressionSyntax conditional => NamingNode(conditional.WhenNotNull),
        Vb.QualifiedNameSyntax qualified => qualified.Right,

        _ => node
    };

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
        Scip.Index index, Roots roots)
    {
        foreach (var trivia in root.GetLeadingTrivia())
        {
            if (trivia.GetStructure() is not PragmaChecksumDirectiveTriviaSyntax checksum) continue;
            var file = checksum.File.ValueText;
            if (!string.IsNullOrEmpty(file))
                GetOrAddDocument(map, index, file, roots);
        }

        foreach (var mapping in tree.GetLineMappings())
        {
            var path = mapping.MappedSpan.Path;
            if (string.IsNullOrEmpty(path)) continue;
            GetOrAddDocument(map, index, path, roots);
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
        Dictionary<string, Scip.Document?> map, Scip.Index index, string path, Roots roots)
    {
        if (map.TryGetValue(path, out var existing)) return existing;

        var relative = RelativeWithinRoot(roots.ProjectRoot, path);
        if (relative is null)
        {
            // Either way the file is named in the index it is absent from, because a
            // reader asking why it is not there is owed the answer. What differs is
            // whether its absence means anything is missing.
            //
            // Constraint 3 is about an incomplete index looking like a complete one, so
            // a gap in the repository is recorded through the channel that degrades the
            // health record and raises the exit code. Somebody else's file is not a gap
            // in this repository: reporting it as one made a stock .NET solution exit 3
            // on every query forever, which is how a banner stops being read.
            index.Metadata.ToolInfo.Arguments.Add(
                (roots.IsExternal(path) ? ExternalDocumentPrefix : OutsideProjectRootPrefix) + path);
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
    /// The SCIP path for a file relative to a root, or null when the file lies outside
    /// that root. Null is therefore also the answer to "is this path outside this
    /// directory", which is what classifying an omission needs.
    ///
    /// scip.proto requires relative_path to use '/' on every platform, and requires
    /// every document to live under Metadata.project_root, so '..' is not available
    /// as an escape hatch. A file genuinely outside the root (source contributed from
    /// the NuGet package cache is the realistic case) therefore cannot be a document
    /// of this index, and the caller records the omission rather than emitting a path
    /// the spec forbids.
    /// </summary>
    private static string? RelativeWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);

        // On Windows a different volume yields the original absolute path back.
        if (Path.IsPathRooted(relative)) return null;

        relative = relative.Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) return null;

        return relative;
    }

    /// <summary>
    /// A path under a known package or SDK location, and so is informational.
    /// </summary>
    private const string ExternalDocumentPrefix = "external-document: ";

    /// <summary>
    /// A path outside project_root that vela cannot attribute to a package or the SDK.
    /// A real coverage gap, and degrades the index.
    /// </summary>
    private const string OutsideProjectRootPrefix = "outside-project-root: ";

    /// <summary>
    /// Where an index is rooted, and which directories hold code that is nobody's
    /// first-party source.
    ///
    /// project_root comes from <see cref="Vela.Indexing.ProjectRoot"/>, which is where
    /// Staleness starts its walk, so an index cannot cover a file OUTSIDE the tree the
    /// freshness check looks at. That is a guarantee about the root and not about the
    /// set of files: Staleness examines only the extensions vela indexes and never
    /// descends into `bin`, `obj`, `.git`, `.vs`, `.idea` or `node_modules`, while this
    /// emitter indexes whatever the compilation hands it, so 365 documents of the real
    /// solution sit under `bin` or `obj` and are indexed without being watched.
    /// </summary>
    private sealed record Roots(string ProjectRoot, IReadOnlyList<string> ExternalRoots)
    {
        public static Roots Resolve(string solutionDirectory) => new(
            Vela.Indexing.ProjectRoot.ForSolutionDirectory(solutionDirectory),
            ExternalRootDirectories());

        /// <summary>
        /// Whether a file vela could not place under project_root belongs to somebody
        /// else rather than being a gap in the repository being indexed.
        ///
        /// A known package or SDK location is the only evidence of that: the NuGet
        /// package cache, which NuGet owns and nobody edits (the case that provoked all
        /// this is Microsoft.NET.Test.Sdk, which contributes a generated entry point
        /// from the cache to every test project), or the .NET installation vela is
        /// running on.
        ///
        /// Merely being outside the repository is NOT evidence, and reading it as such
        /// swallowed first-party code. `&lt;Compile Include="..\..\Shared\Foo.cs" /&gt;`
        /// is an ordinary way to share source between two repositories; a .sln can
        /// reference a project above its own root, in which case every file of that
        /// project is outside; and a submodule puts its parent repository outside,
        /// because the innermost .git wins. All three are the user's own code, absent
        /// from the index, and under Constraint 3 they have to stay loud.
        /// </summary>
        public bool IsExternal(string path) =>
            ExternalRoots.Any(root => RelativeWithinRoot(root, path) is not null);

        /// <summary>
        /// The directories whose contents belong to a package manager or to the .NET
        /// installation rather than to anybody's repository.
        ///
        /// The NuGet package cache is resolved the way NuGet itself resolves it:
        /// NUGET_PACKAGES when it is set, ~/.nuget/packages otherwise. The .NET
        /// installation is the one this process is running on, plus DOTNET_ROOT when it
        /// names a different one. Anything that cannot be resolved is left out, and
        /// files from it then stay loud: reporting a gap that is not one is recoverable,
        /// silently dropping first-party code is not.
        /// </summary>
        private static IReadOnlyList<string> ExternalRootDirectories()
        {
            var roots = new List<string>();

            var configuredCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrWhiteSpace(configuredCache))
            {
                roots.Add(Path.GetFullPath(configuredCache));
            }
            else
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile))
                    roots.Add(Path.Combine(profile, ".nuget", "packages"));
            }

            var configuredDotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(configuredDotnet))
                roots.Add(Path.GetFullPath(configuredDotnet));

            var running = DotnetInstallDirectory();
            if (running is not null) roots.Add(running);

            return roots;
        }

        /// <summary>
        /// The root of the .NET installation this process is running on.
        ///
        /// A framework-dependent .NET lays the runtime out as
        /// &lt;dotnet&gt;/shared/&lt;framework&gt;/&lt;version&gt;, so the install root
        /// is three directories above the runtime directory and the SDK, the targeting
        /// packs and the shared framework all sit under it. The layout is checked
        /// rather than assumed: on a host that does not match it (a self-contained
        /// deployment, say) only the runtime directory itself is claimed, because
        /// walking up from an arbitrary application directory could name a directory
        /// holding the user's own code, which is the mistake this whole classification
        /// exists to stop making.
        /// </summary>
        private static string? DotnetInstallDirectory()
        {
            var runtime = RuntimeEnvironment.GetRuntimeDirectory();
            if (string.IsNullOrEmpty(runtime)) return null;

            var version = new DirectoryInfo(runtime);
            var shared = version.Parent?.Parent;

            return shared?.Parent is not null
                   && string.Equals(shared.Name, "shared", StringComparison.OrdinalIgnoreCase)
                ? shared.Parent.FullName
                : version.FullName;
        }

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

    /// <summary>
    /// The identity of a symbol, as stored in the index and as typed by a user.
    ///
    /// The format above is a deliberate, documented decision for types and members and
    /// is not changed here: a namespace, type, method, property, event or field renders
    /// exactly as it always did, and the queries, the tests and the output all depend
    /// on that.
    ///
    /// It has nothing to say about the kinds that are scoped to a body, though.
    /// ToDisplayString on an ILocalSymbol yields the bare name `status`, and on an
    /// IParameterSymbol `System.Int32 count`, neither of which mentions where the thing
    /// is declared. Two methods each declaring `int count` therefore collapsed into one
    /// symbol, and `refs count` answered with every local of that name in the solution
    /// as though they were a single variable - a count that is not merely imprecise but
    /// describes something that does not exist.
    ///
    /// So those kinds, and only those kinds, are qualified by the symbol that contains
    /// them, which for a local or a parameter is the method and for a type parameter is
    /// the type or method it belongs to. The result reads the way the rest of the
    /// format does: Counter.First().count.
    /// </summary>
    public static string For(ISymbol symbol) => symbol.Kind switch
    {
        SymbolKind.Local or SymbolKind.Parameter or SymbolKind.RangeVariable or SymbolKind.Label
            => QualifiedByContainer(symbol),
        _ => symbol.ToDisplayString(Format)
    };

    private static string QualifiedByContainer(ISymbol symbol)
    {
        var container = symbol.ContainingSymbol;

        // A local in a top level statement, or in any other context Roslyn gives no
        // containing symbol for, keeps its bare name. That is the identity it already
        // had, so nothing is made worse; there is simply nothing to qualify it with.
        if (container is null) return symbol.Name;

        var prefix = container.ToDisplayString(Format);
        return string.IsNullOrEmpty(prefix) ? symbol.Name : prefix + "." + symbol.Name;
    }
}
