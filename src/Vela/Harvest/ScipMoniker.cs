using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Vela.Harvest;

/// <summary>
/// The other name a symbol has: its SCIP moniker.
///
/// vela's own identity for a symbol is a Roslyn display string,
/// `ScentVerdict.Data.Entities.Perfume.Status` (see <see cref="SymbolIdentity"/>). That
/// is what a person types, what every query matches against, and what the whole-dotted-
/// segment rule and the ambiguity tally operate on, and none of it changes here. A SCIP
/// symbol is a different grammar for a different purpose:
///
///     scip-dotnet nuget ScentVerdict.Data 1.0.0.0 ScentVerdict/Data/Entities/Perfume#Status.
///
/// It is what makes an index readable by another SCIP tool and what lets a foreign
/// index be correlated with vela's, so the two names are stored side by side and
/// neither replaces the other.
///
/// The grammar is the one in the comment above `message Symbol` in the vendored
/// src/Vela/Scip/scip.proto, which is the authority:
///
///     symbol     ::= scheme ' ' manager ' ' package-name ' ' version ' ' descriptor+
///                  | 'local ' local-id
///     namespace  ::= name '/'      type           ::= name '#'
///     term       ::= name '.'      meta           ::= name ':'
///     method     ::= name '(' disambiguator? ').'
///     parameter  ::= '(' name ')'  type-parameter ::= '[' name ']'
///
/// The approach - a package prefix, a descriptor per node on the way down the ancestry,
/// backtick escaping for anything that is not a simple identifier, and a `+N`
/// disambiguator for overloads - is ported from sourcegraph/scip-dotnet, specifically
/// ScipDotnet/ScipSymbol.cs and the symbol construction in ScipDotnet/ScipDocumentIndexer.cs
/// (Apache 2.0). No file is vendored, and it deliberately departs from that
/// implementation in four places, each marked below:
///
///  1. A namespace is qualified by the namespaces above it, and by no package.
///     scip-dotnet resolves a namespace's owner straight to the package, which is its
///     open issue #85: it makes Fixture.Deep and Other.Deep the same symbol, and a
///     symbol is required to be a unique identifier across a package. And a namespace
///     belongs to no one assembly, so it takes the grammar's placeholder package
///     rather than whichever of its constituents was reached first (see PackageOf).
///  2. A generic type carries its arity, because C# lets Task and Task&lt;T&gt; live in
///     one namespace and a descriptor of `Task#` for both is the same collision.
///  3. A type parameter is a descriptor, not a local. It is reachable from outside the
///     document that declares it, and the spec says a local symbol MUST only be used
///     for something that is not.
///  4. A simple identifier is ASCII, as the grammar says. scip-dotnet tests it with
///     `\w`, which in .NET matches Unicode letters, so an accented C# identifier goes
///     out unescaped and does not parse.
///
/// An instance is a scope, not a utility: it caches a descriptor chain per symbol and
/// hands out the ids for local symbols, so one instance covers one emitted index.
/// </summary>
public sealed class ScipMoniker
{
    /// <summary>
    /// The scheme every .NET SCIP index in the wild uses, so vela's symbols line up
    /// with scip-dotnet's rather than forming a private namespace of their own.
    /// </summary>
    public const string Scheme = "scip-dotnet";

    /// <summary>The package manager a .NET assembly is distributed by.</summary>
    public const string PackageManager = "nuget";

    /// <summary>The grammar's placeholder for a package part that has no value.</summary>
    private const string EmptyPart = ".";

    private readonly Dictionary<ISymbol, string?> descriptors = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IAssemblySymbol, string> packages = new(SymbolEqualityComparer.Default);

    // Locals are keyed by document as well as by symbol, because scip.proto requires a
    // local symbol to name something that cannot be reached from outside one document.
    // A Razor tree can map one declaration into more than one document, and giving it
    // one id shared between them would be exactly the claim the spec forbids.
    private readonly Dictionary<string, Dictionary<ISymbol, string>> locals =
        new(StringComparer.Ordinal);

    // Ids do not restart per document. The spec only requires that an id be used within
    // one document, and a counter that runs across the index keeps every moniker in it
    // unique, which is what makes the moniker usable as a key.
    private int nextLocalId;

    /// <summary>
    /// The SCIP symbol for a Roslyn symbol as it appears in one document.
    /// </summary>
    /// <param name="documentPath">
    /// The relative path of the document the occurrence is being recorded in. Used only
    /// to scope local symbols, which is the one part of a moniker that is not a property
    /// of the symbol alone.
    /// </param>
    public string For(ISymbol symbol, string documentPath)
    {
        var canonical = Canonicalise(symbol);

        var chain = Descriptors(canonical);

        // An empty chain is the global namespace, which is the root of every ancestry
        // and has no name in either grammar: vela's display name for it is the empty
        // string and SCIP has no descriptor that could spell it. A package prefix with
        // no descriptor after it is not a symbol at all - it does not parse - so it
        // takes the one form that can honestly say "nameless, and only here".
        if (chain is null || chain.Length == 0) return Local(canonical, documentPath);

        return PackageOf(canonical) + chain;
    }

    /// <summary>
    /// The SymbolInformation for a definition: the moniker, the kind, the display name
    /// vela knows it by, and whatever documentation the source carries.
    ///
    /// scip.proto asks for markdown in `documentation` and says new indexers should put
    /// only prose there, not a rendered signature, so this is the text of the doc
    /// comment's summary and remarks and nothing else.
    /// </summary>
    public Scip.SymbolInformation Describe(ISymbol symbol, string documentPath, string displayName)
    {
        var canonical = Canonicalise(symbol);
        var moniker = For(canonical, documentPath);

        var information = new Scip.SymbolInformation
        {
            Symbol = moniker,
            Kind = KindOf(canonical),
            DisplayName = displayName
        };

        information.Documentation.AddRange(DocumentationOf(canonical));

        // A local symbol does not encode where it lives, so without this a consumer
        // cannot place it in a hierarchy at all. scip.proto names this as the field's
        // whole purpose.
        if (moniker.StartsWith(LocalPrefix, StringComparison.Ordinal)
            && canonical.ContainingSymbol is { } container)
        {
            var canonicalContainer = Canonicalise(container);
            var enclosing = Descriptors(canonicalContainer);
            if (enclosing is { Length: > 0 })
                information.EnclosingSymbol = PackageOf(canonicalContainer) + enclosing;
        }

        return information;
    }

    private const string LocalPrefix = "local ";

    /// <summary>
    /// The definition a symbol is a use of. `List&lt;Perfume&gt;` and `List&lt;int&gt;`
    /// are one type with one SCIP symbol, and an extension method called through
    /// instance syntax is the static method that was written.
    ///
    /// vela's display name deliberately does NOT do this: it renders what the code says,
    /// which is what a reader typed and what the queries match. The moniker is the
    /// identity of the declaration, so it does.
    /// </summary>
    private static ISymbol Canonicalise(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced }) symbol = reduced;
        return symbol.OriginalDefinition;
    }

    /// <summary>
    /// The descriptor chain for a symbol, without the package prefix, or null when the
    /// symbol cannot have one and must be a local instead.
    ///
    /// The chain and not the whole moniker is what is cached: the chain is a property of
    /// the symbol alone, while the package part is read off the assembly beside it, and
    /// the two are worth keeping separate because only one of them is expensive.
    /// </summary>
    private string? Descriptors(ISymbol symbol)
    {
        if (descriptors.TryGetValue(symbol, out var cached)) return cached;

        string? chain;
        if (IsLocal(symbol))
        {
            chain = null;
        }
        else if (symbol is INamespaceSymbol { IsGlobalNamespace: true })
        {
            // The root of every ancestry, and not a descriptor of its own: a type in no
            // namespace is named by the package plus its own descriptor.
            chain = string.Empty;
        }
        else
        {
            var owner = symbol is INamespaceSymbol space
                ? space.ContainingNamespace
                : symbol.ContainingSymbol;

            // Deviation 1 from scip-dotnet: the owner of a namespace is the namespace
            // above it, not the package.
            var prefix = owner is null ? string.Empty : Descriptors(owner);

            // Anything under a local is itself local: the parameters and type parameters
            // of a local function have no name that reaches outside the document.
            chain = prefix is null ? null : prefix + Descriptor(symbol);
        }

        descriptors[symbol] = chain;
        return chain;
    }

    /// <summary>
    /// Symbols that cannot be named from outside the document they appear in, and so
    /// must use the `local &lt;id&gt;` form.
    ///
    /// The array, pointer, function pointer and dynamic types are here for a different
    /// reason: SCIP has no descriptor that can name a constructed type, and their Roslyn
    /// name is empty, so there is nothing to build a global symbol out of. Emitting one
    /// anyway would mean every `int[]` in the solution shared a symbol with every other
    /// constructed type, which is worse than admitting the moniker is document-local.
    /// vela's display name still tells the two apart, and that is what the queries use.
    /// </summary>
    private static bool IsLocal(ISymbol symbol) =>
        symbol.Kind is SymbolKind.Local or SymbolKind.RangeVariable or SymbolKind.Label
            or SymbolKind.Discard or SymbolKind.Alias or SymbolKind.Preprocessing
            or SymbolKind.ArrayType or SymbolKind.PointerType
            or SymbolKind.FunctionPointerType or SymbolKind.DynamicType
        || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }
        // An anonymous type and a lambda have no name to escape. The global namespace
        // has no name either and is handled above, not here.
        || (symbol.Name.Length == 0 && symbol.Kind != SymbolKind.Namespace);

    private string Local(ISymbol symbol, string documentPath)
    {
        if (!locals.TryGetValue(documentPath, out var inDocument))
            locals[documentPath] = inDocument = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);

        if (inDocument.TryGetValue(symbol, out var existing)) return existing;

        var id = LocalPrefix + nextLocalId.ToString(CultureInfo.InvariantCulture);
        nextLocalId++;
        inDocument[symbol] = id;
        return id;
    }

    private string Descriptor(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => Escape(symbol.Name) + '/',

        // Deviation 2: the metadata name, so Box`1 and Box are two symbols. Roslyn
        // spells the arity with a backtick, which the grammar carries as an escaped
        // identifier with the backtick doubled.
        INamedTypeSymbol named => Escape(named.MetadataName) + '#',

        ITypeParameterSymbol => "[" + Escape(symbol.Name) + "]",
        IParameterSymbol => "(" + Escape(symbol.Name) + ")",
        IMethodSymbol method => Escape(method.Name) + "(" + Disambiguator(method) + ").",

        // A property, a field and an event are all values a name resolves to.
        // scip-dotnet files an event under the type suffix; an event is not a type.
        IPropertySymbol or IFieldSymbol or IEventSymbol => Escape(symbol.Name) + '.',

        ITypeSymbol => Escape(symbol.MetadataName) + '#',

        // An assembly or a module: named, but not part of anybody's ancestry, so the
        // grammar's catch-all suffix is the honest one.
        _ => Escape(symbol.Name) + ':'
    };

    /// <summary>
    /// What tells two overloads apart, which the grammar provides for methods and
    /// nothing else.
    ///
    /// scip-dotnet counts same-named members in the order the compiler hands them back.
    /// That is declaration order, and declaration order across the files of a partial
    /// class is not something vela can promise is the same on the next run (Constraint
    /// 1: deterministic only). Ordering by the signature instead depends on nothing but
    /// the code: the same solution yields the same disambiguator on any machine, and an
    /// overload added later shifts only the overloads that sort after it.
    /// </summary>
    private static string Disambiguator(IMethodSymbol method)
    {
        var type = method.ContainingType;
        if (type is null) return string.Empty;

        var overloads = type.GetMembers(method.Name).OfType<IMethodSymbol>().ToList();
        if (overloads.Count <= 1) return string.Empty;

        var ordered = overloads
            .OrderBy(SignatureKey, StringComparer.Ordinal)
            .ToList();

        var index = ordered.FindIndex(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, method));
        return index <= 0 ? string.Empty : "+" + index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Everything that can distinguish one overload from another: how many type
    /// parameters it takes, what its parameters are, and its return type, which is the
    /// only thing separating op_Implicit from op_Implicit.
    /// </summary>
    private static string SignatureKey(IMethodSymbol method)
    {
        var definition = method.OriginalDefinition;
        var key = new StringBuilder();
        key.Append(definition.Arity.ToString(CultureInfo.InvariantCulture)).Append('|');

        foreach (var parameter in definition.Parameters)
        {
            key.Append(parameter.RefKind).Append(':');
            key.Append(parameter.Type.ToDisplayString(SignatureFormat)).Append(',');
        }

        key.Append('|').Append(definition.ReturnType.ToDisplayString(SignatureFormat));
        return key.ToString();
    }

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    /// <summary>
    /// The package a symbol is distributed in, or the placeholder when it is in none.
    ///
    /// A namespace is in none. It is not declared by an assembly the way a type is: C#
    /// lets any number of assemblies contribute to one namespace, Roslyn hands the
    /// result back merged with no ContainingAssembly at all, and the merge differs by
    /// compilation, so `Microsoft.AspNetCore` reached through one project and through
    /// the next is the same namespace with two different owners. Naming one of them
    /// picks by traversal order and writes a falsehood that varies run to run: measured
    /// on the real solution, 46% of all namespace occurrences carried more than one
    /// moniker, `ScentVerdict.Data` had ten, and a consumer joining namespace references
    /// across the index - the whole point of carrying a moniker - could not.
    ///
    /// So a namespace says it has no package, which is true and is the same answer from
    /// everywhere. Nothing is lost: the descriptor chain still names the namespace
    /// uniquely, and a type inside it still carries the assembly that defines it.
    /// </summary>
    private string PackageOf(ISymbol symbol) =>
        symbol is INamespaceSymbol ? PlaceholderPackage : Package(symbol.ContainingAssembly);

    /// <summary>
    /// scip.proto, of the manager, the package name and the version alike: "Use the
    /// placeholder '.' to indicate an empty value". All three, because a package part
    /// is not empty here for want of looking it up: there is no package.
    /// </summary>
    private static readonly string PlaceholderPackage =
        Scheme + " " + EmptyPart + " " + EmptyPart + " " + EmptyPart + " ";

    /// <summary>
    /// The package part of a moniker: `scip-dotnet nuget &lt;name&gt; &lt;version&gt; `,
    /// with the grammar's '.' placeholder wherever there is nothing to say. A space
    /// inside any part is escaped by doubling it, as the grammar requires, or the part
    /// boundaries would move.
    /// </summary>
    private string Package(IAssemblySymbol? assembly)
    {
        if (assembly is null) return PlaceholderPackage;
        if (packages.TryGetValue(assembly, out var cached)) return cached;

        var identity = assembly.Identity;
        var name = string.IsNullOrEmpty(identity.Name) ? EmptyPart : identity.Name;
        var version = identity.Version?.ToString() ?? EmptyPart;

        var package = Scheme + " " + PackageManager + " "
            + EscapeSpaces(name) + " " + EscapeSpaces(version) + " ";

        packages[assembly] = package;
        return package;
    }

    private static string EscapeSpaces(string value) => value.Replace(" ", "  ");

    /// <summary>
    /// A name as the grammar wants it: written plainly when it is a simple identifier,
    /// and wrapped in backticks otherwise, with any backtick inside it doubled.
    /// </summary>
    private static string Escape(string name) =>
        IsSimpleIdentifier(name) ? name : "`" + name.Replace("`", "``") + "`";

    /// <summary>
    /// identifier-character ::= '_' | '+' | '-' | '$' | ASCII letter or digit.
    ///
    /// Deviation 4: ASCII, exactly as written. `café` and `Ünterscheidung` are ordinary
    /// C# identifiers and are not simple identifiers here, so they are escaped.
    /// </summary>
    private static bool IsSimpleIdentifier(string name)
    {
        if (name.Length == 0) return false;

        foreach (var c in name)
        {
            var simple = c is '_' or '+' or '-' or '$'
                || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9');
            if (!simple) return false;
        }

        return true;
    }

    /// <summary>
    /// The fine-grained kind scip.proto asks for, which it says to use in preference to
    /// reading the descriptor suffix.
    /// </summary>
    public static Scip.SymbolInformation.Types.Kind KindOf(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => Scip.SymbolInformation.Types.Kind.Namespace,

        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Class => Scip.SymbolInformation.Types.Kind.Class,
            TypeKind.Interface => Scip.SymbolInformation.Types.Kind.Interface,
            TypeKind.Struct => Scip.SymbolInformation.Types.Kind.Struct,
            TypeKind.Enum => Scip.SymbolInformation.Types.Kind.Enum,
            TypeKind.Delegate => Scip.SymbolInformation.Types.Kind.Delegate,
            // Visual Basic's Module, which is the one .NET construct the enum's own
            // Module member describes.
            TypeKind.Module => Scip.SymbolInformation.Types.Kind.Module,
            _ => Scip.SymbolInformation.Types.Kind.UnspecifiedKind
        },

        IMethodSymbol method => method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor
                => Scip.SymbolInformation.Types.Kind.Constructor,
            MethodKind.PropertyGet => Scip.SymbolInformation.Types.Kind.Getter,
            MethodKind.PropertySet => Scip.SymbolInformation.Types.Kind.Setter,
            MethodKind.UserDefinedOperator or MethodKind.BuiltinOperator or MethodKind.Conversion
                => Scip.SymbolInformation.Types.Kind.Operator,
            MethodKind.LocalFunction or MethodKind.AnonymousFunction
                => Scip.SymbolInformation.Types.Kind.Function,
            _ when method.IsAbstract => Scip.SymbolInformation.Types.Kind.AbstractMethod,
            _ when method.IsStatic => Scip.SymbolInformation.Types.Kind.StaticMethod,
            _ => Scip.SymbolInformation.Types.Kind.Method
        },

        IPropertySymbol property => property.IsStatic
            ? Scip.SymbolInformation.Types.Kind.StaticProperty
            : Scip.SymbolInformation.Types.Kind.Property,

        IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum
            => Scip.SymbolInformation.Types.Kind.EnumMember,
        IFieldSymbol { IsConst: true } => Scip.SymbolInformation.Types.Kind.Constant,
        IFieldSymbol { IsStatic: true } => Scip.SymbolInformation.Types.Kind.StaticField,
        IFieldSymbol => Scip.SymbolInformation.Types.Kind.Field,

        IEventSymbol { IsStatic: true } => Scip.SymbolInformation.Types.Kind.StaticEvent,
        IEventSymbol => Scip.SymbolInformation.Types.Kind.Event,

        IParameterSymbol => Scip.SymbolInformation.Types.Kind.Parameter,
        ITypeParameterSymbol => Scip.SymbolInformation.Types.Kind.TypeParameter,
        IAliasSymbol => Scip.SymbolInformation.Types.Kind.TypeAlias,

        // A local, a range variable and a label: the enum has no finer word for any of
        // them than Variable.
        ILocalSymbol or IRangeVariableSymbol or ILabelSymbol
            => Scip.SymbolInformation.Types.Kind.Variable,

        _ => Scip.SymbolInformation.Types.Kind.UnspecifiedKind
    };

    /// <summary>
    /// The prose of a symbol's doc comment: the summary, then the remarks, each as one
    /// markdown paragraph. Nothing is emitted when there is no comment, and a comment
    /// the compiler could not parse is treated as no comment rather than as a failure,
    /// because a malformed doc comment is not a reason to lose an index.
    /// </summary>
    private static IReadOnlyList<string> DocumentationOf(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(CultureInfo.InvariantCulture, expandIncludes: false);
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<string>();

        XElement root;
        try
        {
            root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<string>();
        }

        var prose = new List<string>();
        foreach (var name in new[] { "summary", "remarks" })
        {
            foreach (var element in root.Elements(name))
            {
                var text = Reflow(element.Value);
                if (text.Length > 0) prose.Add(text);
            }
        }

        return prose;
    }

    /// <summary>
    /// A doc comment's text with the leading `///` indentation taken off each line and
    /// the blank lines around it removed, so what is left is readable as markdown.
    /// </summary>
    private static string Reflow(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();

        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        return string.Join("\n", lines);
    }
}
