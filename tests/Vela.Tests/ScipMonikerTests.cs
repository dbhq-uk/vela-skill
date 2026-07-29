using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vela.Harvest;
using Xunit;

/// <summary>
/// The SCIP symbol grammar, as it is written in the comment above `message Symbol` in
/// the vendored src/Vela/Scip/scip.proto, implemented as a parser.
///
/// Asserting string equality on a handful of examples proves that those examples came
/// out as expected and nothing else. A parser is the stronger claim: it says the emitted
/// moniker is a sentence in the grammar, that its parts are the parts vela meant, and,
/// because <see cref="Render"/> reconstructs the input exactly, that nothing was lost or
/// silently reinterpreted on the way.
///
/// It lives in the test project deliberately. Emitting and parsing with the same code
/// would make a round trip prove only that a function is its own inverse.
/// </summary>
internal static class ScipSymbolGrammar
{
    internal sealed record Descriptor(string Name, string Suffix, string Disambiguator = "");

    internal sealed record Symbol(
        string Scheme,
        string Manager,
        string PackageName,
        string Version,
        IReadOnlyList<Descriptor> Descriptors,
        string? LocalId)
    {
        public bool IsLocal => LocalId is not null;
    }

    internal const string Namespace = "namespace";
    internal const string Type = "type";
    internal const string Term = "term";
    internal const string Method = "method";
    internal const string TypeParameter = "type-parameter";
    internal const string Parameter = "parameter";
    internal const string Meta = "meta";
    internal const string Macro = "macro";

    /// <summary>
    /// Parses a symbol, or throws <see cref="FormatException"/> naming what the grammar
    /// wanted and where.
    /// </summary>
    public static Symbol Parse(string symbol)
    {
        if (symbol.StartsWith("local ", StringComparison.Ordinal))
        {
            var id = symbol["local ".Length..];
            if (id.Length == 0 || !id.All(IsIdentifierCharacter))
                throw new FormatException($"'{symbol}': a local id must be a simple identifier");
            return new Symbol("", "", "", "", Array.Empty<Descriptor>(), id);
        }

        var pos = 0;
        var scheme = ReadSpaceEscaped(symbol, ref pos, "scheme");
        if (scheme.Length == 0)
            throw new FormatException($"'{symbol}': the scheme must not be empty");
        if (scheme.StartsWith("local", StringComparison.Ordinal))
            throw new FormatException($"'{symbol}': the scheme must not start with 'local'");

        var manager = ReadSpaceEscaped(symbol, ref pos, "manager");
        var packageName = ReadSpaceEscaped(symbol, ref pos, "package name");
        var version = ReadSpaceEscaped(symbol, ref pos, "version");

        var descriptors = new List<Descriptor>();
        while (pos < symbol.Length) descriptors.Add(ReadDescriptor(symbol, ref pos));

        if (descriptors.Count == 0)
            throw new FormatException($"'{symbol}': a global symbol needs at least one descriptor");

        return new Symbol(scheme, manager, packageName, version, descriptors, null);
    }

    /// <summary>Writes a parsed symbol back out, so a round trip can be compared.</summary>
    public static string Render(Symbol symbol)
    {
        if (symbol.IsLocal) return "local " + symbol.LocalId;

        var text = new StringBuilder();
        text.Append(EscapeSpaces(symbol.Scheme)).Append(' ');
        text.Append(EscapeSpaces(symbol.Manager)).Append(' ');
        text.Append(EscapeSpaces(symbol.PackageName)).Append(' ');
        text.Append(EscapeSpaces(symbol.Version)).Append(' ');

        foreach (var descriptor in symbol.Descriptors)
        {
            var name = EscapeName(descriptor.Name);
            switch (descriptor.Suffix)
            {
                case Namespace: text.Append(name).Append('/'); break;
                case Type: text.Append(name).Append('#'); break;
                case Term: text.Append(name).Append('.'); break;
                case Meta: text.Append(name).Append(':'); break;
                case Macro: text.Append(name).Append('!'); break;
                case Method:
                    text.Append(name).Append('(').Append(descriptor.Disambiguator).Append(")."); break;
                case Parameter: text.Append('(').Append(name).Append(')'); break;
                case TypeParameter: text.Append('[').Append(name).Append(']'); break;
                default: throw new FormatException("unknown suffix " + descriptor.Suffix);
            }
        }

        return text.ToString();
    }

    /// <summary>Parses, then re-renders, and fails if the two differ.</summary>
    public static Symbol RoundTrip(string symbol)
    {
        var parsed = Parse(symbol);
        var rendered = Render(parsed);
        if (!string.Equals(rendered, symbol, StringComparison.Ordinal))
            throw new FormatException(
                $"'{symbol}' does not survive a round trip through the grammar; it came back as '{rendered}'");
        return parsed;
    }

    private static string EscapeSpaces(string value) => value.Replace(" ", "  ");

    private static string EscapeName(string name) =>
        name.Length > 0 && name.All(IsIdentifierCharacter)
            ? name
            : "`" + name.Replace("`", "``") + "`";

    private static string ReadSpaceEscaped(string symbol, ref int pos, string part)
    {
        var value = new StringBuilder();
        while (true)
        {
            if (pos >= symbol.Length)
                throw new FormatException($"'{symbol}': ended while reading the {part}");

            var c = symbol[pos];
            if (c == ' ')
            {
                // A doubled space is an escaped space; a single one ends the part.
                if (pos + 1 < symbol.Length && symbol[pos + 1] == ' ')
                {
                    value.Append(' ');
                    pos += 2;
                    continue;
                }

                pos++;
                return value.ToString();
            }

            value.Append(c);
            pos++;
        }
    }

    private static Descriptor ReadDescriptor(string symbol, ref int pos)
    {
        // A parameter and a type parameter wrap their name, so they are recognised
        // before a name is read; every other descriptor is a name plus a suffix.
        if (symbol[pos] == '(')
        {
            pos++;
            var parameter = ReadName(symbol, ref pos);
            Expect(symbol, ref pos, ')');
            return new Descriptor(parameter, Parameter);
        }

        if (symbol[pos] == '[')
        {
            pos++;
            var typeParameter = ReadName(symbol, ref pos);
            Expect(symbol, ref pos, ']');
            return new Descriptor(typeParameter, TypeParameter);
        }

        var name = ReadName(symbol, ref pos);
        if (pos >= symbol.Length)
            throw new FormatException($"'{symbol}': descriptor '{name}' has no suffix");

        var suffix = symbol[pos++];
        switch (suffix)
        {
            case '/': return new Descriptor(name, Namespace);
            case '#': return new Descriptor(name, Type);
            case '.': return new Descriptor(name, Term);
            case ':': return new Descriptor(name, Meta);
            case '!': return new Descriptor(name, Macro);
            case '(':
                var disambiguator = new StringBuilder();
                while (pos < symbol.Length && symbol[pos] != ')') disambiguator.Append(symbol[pos++]);
                Expect(symbol, ref pos, ')');
                Expect(symbol, ref pos, '.');
                var value = disambiguator.ToString();
                if (value.Length > 0 && !value.All(IsIdentifierCharacter))
                    throw new FormatException(
                        $"'{symbol}': the method disambiguator '{value}' is not a simple identifier");
                return new Descriptor(name, Method, value);
            default:
                throw new FormatException($"'{symbol}': '{suffix}' is not a descriptor suffix");
        }
    }

    private static string ReadName(string symbol, ref int pos)
    {
        if (pos < symbol.Length && symbol[pos] == '`')
        {
            pos++;
            var value = new StringBuilder();
            while (true)
            {
                if (pos >= symbol.Length)
                    throw new FormatException($"'{symbol}': an escaped identifier was never closed");

                if (symbol[pos] == '`')
                {
                    // A doubled backtick is a literal one; a single one closes the name.
                    if (pos + 1 < symbol.Length && symbol[pos + 1] == '`')
                    {
                        value.Append('`');
                        pos += 2;
                        continue;
                    }

                    pos++;
                    break;
                }

                value.Append(symbol[pos++]);
            }

            var escaped = value.ToString();
            if (escaped.Length == 0)
                throw new FormatException($"'{symbol}': an escaped identifier must not be empty");

            // The grammar requires an escaped identifier to hold at least one character
            // that could not have been written plainly, so escaping is never optional.
            if (escaped.All(IsIdentifierCharacter))
                throw new FormatException(
                    $"'{symbol}': '{escaped}' is a simple identifier and must not be escaped");

            return escaped;
        }

        var start = pos;
        while (pos < symbol.Length && IsIdentifierCharacter(symbol[pos])) pos++;
        if (pos == start)
            throw new FormatException($"'{symbol}': expected an identifier at offset {start}");

        return symbol[start..pos];
    }

    private static void Expect(string symbol, ref int pos, char expected)
    {
        if (pos >= symbol.Length || symbol[pos] != expected)
            throw new FormatException($"'{symbol}': expected '{expected}' at offset {pos}");
        pos++;
    }

    /// <summary>
    /// identifier-character ::= '_' | '+' | '-' | '$' | ASCII letter or digit.
    /// ASCII, exactly as the grammar says: an accented letter is a perfectly good C#
    /// identifier and is not a simple identifier here, so it has to be escaped.
    /// </summary>
    private static bool IsIdentifierCharacter(char c) =>
        c is '_' or '+' or '-' or '$'
        || (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9');
}

public class ScipMonikerTests
{
    /// <summary>
    /// One file holding every shape the grammar has a descriptor for. The assembly
    /// version is declared rather than defaulted, because the package version is part
    /// of a SCIP symbol and a value that is always 0.0.0.0 would prove nothing.
    /// </summary>
    private const string Source = """
        [assembly: System.Reflection.AssemblyVersion("4.2.1.0")]

        namespace Fixture.Deep;

        public interface IThing
        {
            void Do();
        }

        public class Outer : IThing
        {
            public class Nested
            {
                public int Depth;
            }

            public int Field;

            // Not a simple identifier: 'é' is a letter C# accepts and the SCIP grammar
            // does not, so the name has to be escaped.
            public int café;

            public string Status { get; set; } = "";

            public Outer() { }

            public Outer(int seed) { Field = seed; }

            public void NoParameters() { }

            public int WithParameters(int count, string label)
            {
                var total = count + label.Length;
                return total;
            }

            void IThing.Do() { }
        }

        public class Box<TItem>
        {
            public TItem? Item { get; set; }
        }
        """;

    private const string Prefix = "scip-dotnet nuget Fixture 4.2.1.0 ";

    /// <summary>
    /// What a namespace is prefixed with instead: the grammar's placeholder for a
    /// package part with no value, in all three parts, because a namespace is in no
    /// package at all.
    /// </summary>
    private const string NamespacePrefix = "scip-dotnet . . . ";

    [Fact]
    public void For_QualifiesANamespaceByEveryNamespaceAboveIt()
    {
        var (compilation, moniker) = Setup();

        var fixture = compilation.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "Fixture");
        var deep = fixture.GetNamespaceMembers().Single(n => n.Name == "Deep");

        // A namespace has no package (see the test above), so the chain is what carries
        // the whole of its identity and every namespace above it has to be in it.
        Assert.Equal(NamespacePrefix + "Fixture/", moniker.For(fixture, Document));

        // scip-dotnet's own issue #85 is that this comes out as 'Deep/', because it
        // resolves a namespace's owner straight to the package instead of to the
        // namespace above it. A symbol has to be a unique identifier across a package,
        // and Fixture.Deep and Other.Deep are not the same namespace.
        Assert.Equal(NamespacePrefix + "Fixture/Deep/", moniker.For(deep, Document));
    }

    [Fact]
    public void For_GivesANamespaceThePlaceholderPackageWhicheverCompilationReachedIt()
    {
        // A namespace declared in more than one assembly is handed back merged and
        // belongs to none of them, so choosing one of its constituents files the same
        // namespace under a different package depending on which compilation reached it.
        // Measured on the real solution, 46% of all namespace occurrences carried more
        // than one moniker that way, and a consumer cannot join a namespace reference
        // across an index whose monikers depend on traversal order.
        //
        // scip.proto: "<manager> ::= any UTF-8, escape spaces with double space. Use the
        // placeholder '.' to indicate an empty value", and the same for the package name
        // and the version. A namespace has no package, so it says so.
        var alpha = Compile("Alpha", "namespace Shared; public class A { }");
        var beta = Compile("Beta", "namespace Shared; public class B { }", alpha.ToMetadataReference());

        var fromAlpha = alpha.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "Shared");
        var fromBeta = beta.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "Shared");

        // The namespace Beta sees really is the merged one: declared in both assemblies.
        Assert.Equal(2, fromBeta.ConstituentNamespaces.Length);

        var moniker = new ScipMoniker();

        Assert.Equal("scip-dotnet . . . Shared/", moniker.For(fromAlpha, Document));
        Assert.Equal(moniker.For(fromAlpha, Document), moniker.For(fromBeta, Document));
        ScipSymbolGrammar.RoundTrip(moniker.For(fromBeta, Document));

        // And it is scoped to namespaces: a type still names the assembly that defines
        // it, which is what makes a type moniker correlatable in the first place.
        var a = beta.GetTypeByMetadataName("Shared.A")!;
        Assert.Equal("scip-dotnet nuget Alpha 0.0.0.0 Shared/A#", moniker.For(a, Document));
    }

    [Fact]
    public void For_NamesATypeANestedTypeAndAGenericType()
    {
        var (compilation, moniker) = Setup();

        var outer = compilation.GetTypeByMetadataName("Fixture.Deep.Outer")!;
        var nested = outer.GetTypeMembers("Nested").Single();
        var box = compilation.GetTypeByMetadataName("Fixture.Deep.Box`1")!;

        Assert.Equal(Prefix + "Fixture/Deep/Outer#", moniker.For(outer, Document));
        Assert.Equal(Prefix + "Fixture/Deep/Outer#Nested#", moniker.For(nested, Document));

        // Box<TItem> and a non-generic Box could sit side by side in one namespace, so
        // the arity is part of the name. Roslyn's metadata name carries it, and it
        // carries it with a backtick, which the grammar makes an escaped identifier
        // with the backtick doubled.
        Assert.Equal(Prefix + "Fixture/Deep/`Box``1`#", moniker.For(box, Document));
    }

    [Fact]
    public void For_NamesMethodsPropertiesFieldsAndConstructors()
    {
        var (compilation, moniker) = Setup();
        var outer = compilation.GetTypeByMetadataName("Fixture.Deep.Outer")!;

        var noParameters = outer.GetMembers("NoParameters").Single();
        var withParameters = (IMethodSymbol)outer.GetMembers("WithParameters").Single();
        var status = outer.GetMembers("Status").OfType<IPropertySymbol>().Single();
        var field = outer.GetMembers("Field").OfType<IFieldSymbol>().Single();
        var accented = outer.GetMembers("café").OfType<IFieldSymbol>().Single();

        Assert.Equal(Prefix + "Fixture/Deep/Outer#NoParameters().", moniker.For(noParameters, Document));
        Assert.Equal(Prefix + "Fixture/Deep/Outer#WithParameters().", moniker.For(withParameters, Document));
        Assert.Equal(Prefix + "Fixture/Deep/Outer#Status.", moniker.For(status, Document));
        Assert.Equal(Prefix + "Fixture/Deep/Outer#Field.", moniker.For(field, Document));
        Assert.Equal(Prefix + "Fixture/Deep/Outer#`café`.", moniker.For(accented, Document));

        // A constructor is called .ctor in metadata, and a dot is not an identifier
        // character, so the name is escaped. The two overloads are told apart by the
        // disambiguator the grammar provides for exactly this.
        var constructors = outer.InstanceConstructors
            .Select(c => moniker.For(c, Document))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                Prefix + "Fixture/Deep/Outer#`.ctor`().",
                Prefix + "Fixture/Deep/Outer#`.ctor`(+1)."
            },
            constructors);
    }

    [Fact]
    public void For_NamesParametersAndTypeParametersWithTheirOwnBrackets()
    {
        var (compilation, moniker) = Setup();

        var outer = compilation.GetTypeByMetadataName("Fixture.Deep.Outer")!;
        var withParameters = (IMethodSymbol)outer.GetMembers("WithParameters").Single();
        var box = compilation.GetTypeByMetadataName("Fixture.Deep.Box`1")!;

        Assert.Equal(
            Prefix + "Fixture/Deep/Outer#WithParameters().(count)",
            moniker.For(withParameters.Parameters[0], Document));
        Assert.Equal(
            Prefix + "Fixture/Deep/Outer#WithParameters().(label)",
            moniker.For(withParameters.Parameters[1], Document));

        // A type parameter is reachable from outside the document that declares it,
        // so it is a descriptor rather than a local, which is where scip-dotnet
        // differs: it makes every type parameter a `local`.
        Assert.Equal(
            Prefix + "Fixture/Deep/`Box``1`#[TItem]",
            moniker.For(box.TypeParameters[0], Document));
    }

    [Fact]
    public void For_EscapesANameThatIsNotASimpleIdentifier()
    {
        var (compilation, moniker) = Setup();
        var outer = compilation.GetTypeByMetadataName("Fixture.Deep.Outer")!;

        // An explicit interface implementation is named for the interface it
        // implements, dots and all.
        var explicitImplementation = outer
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Single(m => m.ExplicitInterfaceImplementations.Length > 0);

        Assert.Equal(
            Prefix + "Fixture/Deep/Outer#`Fixture.Deep.IThing.Do`().",
            moniker.For(explicitImplementation, Document));
    }

    [Fact]
    public void For_GivesALocalTheLocalFormAndScopesItToItsDocument()
    {
        var (compilation, moniker) = Setup();
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);

        var declarator = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "total");
        var local = model.GetDeclaredSymbol(declarator)!;

        var here = moniker.For(local, Document);
        Assert.Matches("^local [0-9A-Za-z_$+-]+$", here);

        // Asked for again in the same document it is the same local; asked for in
        // another document it is a different one, because the spec allows a local
        // symbol to name an entity only within one document.
        Assert.Equal(here, moniker.For(local, Document));
        Assert.NotEqual(here, moniker.For(local, "App/Other.cs"));
    }

    [Fact]
    public void For_EmitsNoMonikerRatherThanAFalseLocalForWhatItCannotName()
    {
        // scip.proto: "Local symbols MUST only be used for entities which are local to a
        // Document, and cannot be accessed from outside the Document", and it forecloses
        // the excuse directly: "the decision to use a local symbol or global symbol
        // should exclusively be determined whether the local symbol is accessible
        // outside the document, not by the capability to find the enclosing symbol."
        //
        // A string[] is not document-local, and neither is the global namespace or a
        // global using alias. SCIP has no descriptor that can name any of them, but the
        // answer to that is silence: Occurrence.symbol is optional, and an absent symbol
        // is a smaller claim while `local 7` is a false one that an importing consumer
        // will act on.
        var (compilation, moniker) = Setup();
        var csharp = (CSharpCompilation)compilation;

        var array = compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_String));
        var pointer = compilation.CreatePointerTypeSymbol(compilation.GetSpecialType(SpecialType.System_Int32));

        Assert.Equal("", moniker.For(array, Document));
        Assert.Equal("", moniker.For(pointer, Document));
        Assert.Equal("", moniker.For(csharp.DynamicType, Document));
        Assert.Equal("", moniker.For(compilation.GlobalNamespace, Document));

        // A global using alias is spelled once and usable from every document in the
        // compilation, which is the opposite of what `local` asserts.
        var aliases = Compile("Aliases", "global using Text = System.String;");
        var tree = aliases.SyntaxTrees.Single();
        var directive = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().Single();
        var alias = aliases.GetSemanticModel(tree).GetDeclaredSymbol(directive)!;

        Assert.Equal("", moniker.For(alias, Document));
    }

    [Fact]
    public void For_KeepsTheLocalFormForWhatIsGenuinelyLocalToOneDocument()
    {
        // The other direction. A local variable, a label, a local function and anything
        // declared inside one cannot be named from another document, so for them the
        // `local` form is true and is what the spec asks for.
        var body = Compile("Body", """
            public class Body
            {
                public void Go(int seed)
                {
                    var total = seed;
                    Again:
                    int Helper(int step) => step + total;
                    if (total < 0) goto Again;
                }
            }
            """);

        var tree = body.SyntaxTrees.Single();
        var model = body.GetSemanticModel(tree);
        var moniker = new ScipMoniker();

        ISymbol Declared<T>(Func<T, bool> predicate) where T : SyntaxNode =>
            model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<T>().Single(predicate))!;

        var total = Declared<VariableDeclaratorSyntax>(v => v.Identifier.ValueText == "total");
        var label = Declared<LabeledStatementSyntax>(_ => true);
        var helper = Declared<LocalFunctionStatementSyntax>(_ => true);
        var step = Declared<ParameterSyntax>(p => p.Identifier.ValueText == "step");
        var seed = Declared<ParameterSyntax>(p => p.Identifier.ValueText == "seed");

        foreach (var local in new[] { total, label, helper, step })
        {
            var id = moniker.For(local, Document);
            Assert.StartsWith("local ", id, StringComparison.Ordinal);
            ScipSymbolGrammar.RoundTrip(id);
        }

        // And the contrast that makes the rule a rule: `step` is local only because
        // Helper is, while `seed` belongs to a method anyone can call and is named in
        // full.
        Assert.Equal(
            "scip-dotnet nuget Body 0.0.0.0 Body#Go().(seed)",
            moniker.For(seed, Document));
    }

    [Fact]
    public void For_ProducesSymbolsThatParseAgainstTheGrammarInScipProto()
    {
        var (compilation, moniker) = Setup();

        var symbols = AllDeclared(compilation)
            .Select(s => (Symbol: s, Moniker: moniker.For(s, Document)))
            .GroupBy(pair => pair.Moniker, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        Assert.True(symbols.Count > 15, $"the fixture should exercise more than a handful of shapes, saw {symbols.Count}");

        foreach (var (symbol, text) in symbols)
        {
            var parsed = ScipSymbolGrammar.RoundTrip(text);
            if (parsed.IsLocal) continue;

            Assert.Equal("scip-dotnet", parsed.Scheme);
            Assert.NotEmpty(parsed.Descriptors);

            // Everything but a namespace names the package that defines it. A namespace
            // is in no package and says so, in all three parts.
            if (symbol is INamespaceSymbol)
            {
                Assert.Equal(".", parsed.Manager);
                Assert.Equal(".", parsed.PackageName);
                Assert.Equal(".", parsed.Version);
            }
            else
            {
                Assert.Equal("nuget", parsed.Manager);
                Assert.Equal("Fixture", parsed.PackageName);
                Assert.Equal("4.2.1.0", parsed.Version);
            }
        }
    }

    [Fact]
    public void For_TakesThePackageFromTheAssemblyThatDefinesTheSymbol()
    {
        // Not from the assembly being indexed. A reference to String belongs to the
        // framework and has to say so, or an index could never be correlated with
        // anybody else's index of the same library.
        var (compilation, moniker) = Setup();

        var text = compilation.GetSpecialType(SpecialType.System_String);
        var parsed = ScipSymbolGrammar.RoundTrip(moniker.For(text, Document));

        Assert.Equal(text.ContainingAssembly.Identity.Name, parsed.PackageName);
        Assert.Equal(text.ContainingAssembly.Identity.Version.ToString(), parsed.Version);
        Assert.NotEqual("Fixture", parsed.PackageName);

        Assert.Equal(
            new[] { ("System", ScipSymbolGrammar.Namespace), ("String", ScipSymbolGrammar.Type) },
            parsed.Descriptors.Select(d => (d.Name, d.Suffix)));
    }

    [Fact]
    public void Describe_CarriesTheKindTheDocumentationAndTheDisplayNameOfADefinition()
    {
        var source = """
            namespace Fixture;

            public class Widget
            {
                /// <summary>
                /// How many there are.
                /// </summary>
                public int Count { get; set; }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "Fixture",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(documentationMode: DocumentationMode.Parse)) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var moniker = new ScipMoniker();
        var count = compilation.GetTypeByMetadataName("Fixture.Widget")!.GetMembers("Count").Single();

        var information = moniker.Describe(count, Document);

        Assert.Equal(moniker.For(count, Document), information.Symbol);
        Assert.Equal(Scip.SymbolInformation.Types.Kind.Property, information.Kind);
        Assert.Contains("How many there are.", Assert.Single(information.Documentation));

        // scip.proto, of display_name: "the symbol 'com/example/MyClass#myMethod(+1).'
        // should have the display name 'myMethod'". The short name, in other words, and
        // not the qualified path: a foreign consumer renders this field in a symbol
        // list, where the qualification is already the column beside it. vela's own
        // identity for the symbol is unaffected - it lives in occurrence.symbol, which
        // is a different field answering a different question.
        Assert.Equal("Count", information.DisplayName);
    }

    [Fact]
    public void Describe_PutsTheShortNameInDisplayNameForEveryShapeOfSymbol()
    {
        var (compilation, moniker) = Setup();
        var outer = compilation.GetTypeByMetadataName("Fixture.Deep.Outer")!;
        var box = compilation.GetTypeByMetadataName("Fixture.Deep.Box`1")!;
        var deep = compilation.GlobalNamespace
            .GetNamespaceMembers().Single(n => n.Name == "Fixture")
            .GetNamespaceMembers().Single(n => n.Name == "Deep");

        string Displayed(ISymbol symbol) => moniker.Describe(symbol, Document).DisplayName;

        Assert.Equal("Deep", Displayed(deep));
        Assert.Equal("Outer", Displayed(outer));
        Assert.Equal("WithParameters", Displayed(outer.GetMembers("WithParameters").Single()));
        Assert.Equal("Status", Displayed(outer.GetMembers("Status").OfType<IPropertySymbol>().Single()));
        Assert.Equal("café", Displayed(outer.GetMembers("café").OfType<IFieldSymbol>().Single()));

        // The descriptor spells a generic type with its arity, because that is what
        // tells Box and Box<T> apart. A reader is not owed the backtick: scip.proto
        // says the symbol "may encode names with special characters that should not be
        // displayed to the user", which is this exactly.
        Assert.EndsWith("`Box``1`#", moniker.For(box, Document), StringComparison.Ordinal);
        Assert.Equal("Box", Displayed(box));
    }

    [Fact]
    public void Describe_NamesTheEnclosingSymbolOfALocalSoItCanStillBePlaced()
    {
        var (compilation, moniker) = Setup();
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);

        var declarator = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "total");
        var local = model.GetDeclaredSymbol(declarator)!;

        var information = moniker.Describe(local, Document);

        Assert.StartsWith("local ", information.Symbol, StringComparison.Ordinal);
        Assert.Equal("total", information.DisplayName);
        Assert.Equal(Scip.SymbolInformation.Types.Kind.Variable, information.Kind);

        // scip.proto: a local symbol does not encode where it lives, so enclosing_symbol
        // is how a consumer places it in a hierarchy. It is a symbol, so it is the
        // qualified moniker and not the short name that goes here.
        Assert.Equal(Prefix + "Fixture/Deep/Outer#WithParameters().", information.EnclosingSymbol);
    }

    private const string Document = "App/Thing.cs";

    private static IEnumerable<ISymbol> AllDeclared(Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);

        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var declared = model.GetDeclaredSymbol(node);
            if (declared is not null) yield return declared;
        }
    }

    /// <summary>One assembly's worth of source, for the tests that need two of them.</summary>
    private static CSharpCompilation Compile(string assemblyName, string source, params MetadataReference[] extra) =>
        CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            References.Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static (Compilation Compilation, ScipMoniker Moniker) Setup()
    {
        var compilation = CSharpCompilation.Create(
            "Fixture",
            new[] { CSharpSyntaxTree.ParseText(Source) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, "the fixture must compile: " + string.Join(" | ", errors));

        return (compilation, new ScipMoniker());
    }

    /// <summary>
    /// The whole framework this test process is running on, so the fixture can use
    /// ordinary BCL types without each one needing its own reference.
    /// </summary>
    private static readonly IReadOnlyList<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToList();
}
