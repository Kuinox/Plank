using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen;

internal static class SchemaProperties
{
    internal static ImmutableArray<IPropertySymbol> GetProperties(INamedTypeSymbol type)
    {
        var properties = new List<IPropertySymbol>();
        var hiddenNames = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                // Member access binds to the most-derived declaration, including overrides
                // and members which hide a base property with a different kind of member.
                if (!hiddenNames.Add(member.Name) || member is not IPropertySymbol property ||
                    property.IsStatic || property.IsIndexer || property.IsImplicitlyDeclared)
                    continue;
                if (!SymbolEqualityComparer.Default.Equals(current, type) &&
                    property.DeclaredAccessibility != Accessibility.Public)
                    continue;
                properties.Add(property);
            }
        }

        return properties
            .OrderBy(static property => property.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static property => property.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
            .ThenBy(static property => property.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
