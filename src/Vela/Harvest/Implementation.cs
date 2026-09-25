using Microsoft.CodeAnalysis;

namespace Vela.Harvest;

/// <summary>
/// What a declared symbol implements, overrides or derives from, as the compiler sees it.
///
/// "What implements this" was a question vela could not answer: it recorded where each
/// symbol is declared and used, and nothing about how one declaration stands for another.
/// The answers here come from Roslyn's own model of the type, so they are exact in the
/// same way the rest of the index is (Constraint 1).
///
/// For a type: every interface it implements, directly or through a base, and every base
/// class above it except the ones every type of its kind has (System.Object, ValueType,
/// Enum, Delegate and MulticastDelegate), which would make `impls Object` a list of every
/// class in the solution. For a method, property or event: every member it overrides, up
/// the chain, and every interface member it or any of those implements, explicitly or
/// implicitly.
/// </summary>
public static class Implementation
{
    public static IEnumerable<ISymbol> Of(ISymbol symbol)
    {
        switch (symbol)
        {
            case INamedTypeSymbol type:
                foreach (var implemented in type.AllInterfaces) yield return implemented;

                if (type.TypeKind is TypeKind.Class)
                {
                    for (var baseType = type.BaseType; baseType is not null && !IsUniversalBase(baseType);
                         baseType = baseType.BaseType)
                        yield return baseType;
                }

                yield break;

            case IMethodSymbol or IPropertySymbol or IEventSymbol:
                // An override stands for what it overrides, and so for every interface member
                // that overridden member implements: a call through the interface reaches it.
                foreach (var member in InterfaceMembers(symbol)) yield return member;
                foreach (var overridden in Overridden(symbol))
                {
                    yield return overridden;
                    foreach (var member in InterfaceMembers(overridden)) yield return member;
                }

                yield break;
        }
    }

    private static IEnumerable<ISymbol> Overridden(ISymbol member)
    {
        for (var current = OverriddenBy(member); current is not null; current = OverriddenBy(current))
            yield return current;
    }

    private static ISymbol? OverriddenBy(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null
    };

    /// <summary>
    /// The interface members this member stands for. Explicit implementations name
    /// themselves; an implicit one is found the way the compiler finds it, by asking the
    /// containing type which of its members implements each interface member. Only the
    /// interface members of the same name are asked about, because an implicit
    /// implementation has the name of what it implements, and asking about every member of
    /// every interface would cost a lookup per pair on every method in the solution.
    /// </summary>
    private static IEnumerable<ISymbol> InterfaceMembers(ISymbol member)
    {
        IEnumerable<ISymbol> explicitly = member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol e => e.ExplicitInterfaceImplementations,
            _ => Array.Empty<ISymbol>()
        };

        var found = new List<ISymbol>(explicitly);

        if (member.ContainingType is { } type && !member.IsStatic)
        {
            foreach (var implemented in type.AllInterfaces)
            {
                foreach (var candidate in implemented.GetMembers(member.Name))
                {
                    if (candidate.Kind != member.Kind) continue;
                    if (found.Contains(candidate, SymbolEqualityComparer.Default)) continue;

                    var implementation = type.FindImplementationForInterfaceMember(candidate);
                    if (SymbolEqualityComparer.Default.Equals(implementation, member))
                        found.Add(candidate);
                }
            }
        }

        return found;
    }

    private static bool IsUniversalBase(INamedTypeSymbol type) => type.SpecialType is
        SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum
        or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate;
}
