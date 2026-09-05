using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen;

/// <summary>Allocates generated identifiers without changing the names of schema properties.</summary>
sealed class GeneratedMemberNames
{
    readonly HashSet<string> reserved;
    readonly HashSet<string> propertyNames;
    readonly Dictionary<string, HashSet<string>> memberScopes = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> allocated = new(StringComparer.Ordinal);

    internal GeneratedMemberNames(INamedTypeSymbol schemaType)
    {
        reserved = new HashSet<string>(schemaType.GetMembers().Select(static member => member.Name),
            StringComparer.Ordinal) { schemaType.Name };
        propertyNames = new HashSet<string>(schemaType.GetMembers().OfType<IPropertySymbol>()
            .Where(static property => !property.IsStatic && !property.IsIndexer && !property.IsImplicitlyDeclared)
            .Select(static property => property.Name), StringComparer.Ordinal);
        // Allocate stable API names before property-derived helpers can claim one of them.
        foreach (var name in new[]
        {
            "Schema", "Writer", "Reader", "DatasetWriter", "Route", "Row", "ReadRow", "Projection",
            "RowCursor", "RowReader", "ReadRowGroup", "ReadRowGroupCollection", "SchemaWriter", "RowGroup",
            "PipelineWriter", "BufferSlot", "CreateDatasetWriter", "CreateRowWriter", "CreateRowReader",
            "CreateReader", "CreateWriter", "s_rowApiColumns", "GeneratedNestedToUnixMicroseconds"
        })
            Root(name);
    }

    internal string Root(string name) => Get("api:" + name, name);

    internal string Helper(string name) => Get("helper:" + name, name);

    internal string Get(string key, string preferredName)
    {
        if (allocated.TryGetValue(key, out var existing))
            return existing;

        // These APIs share a nested type with column properties, not with arbitrary schema members.
        // A schema named All, for example, must retain the otherwise non-conflicting Projection.All.
        var scope = key.StartsWith("projection:", StringComparison.Ordinal) ? "projection"
            : key.StartsWith("row-group:", StringComparison.Ordinal) ? "row-group"
            : key.StartsWith("setter:", StringComparison.Ordinal) ? "row"
            : key.StartsWith("cursor:", StringComparison.Ordinal) ? "cursor" : null;
        var names = reserved;
        if (scope is not null && !memberScopes.TryGetValue(scope, out names))
        {
            names = new HashSet<string>(propertyNames, StringComparer.Ordinal);
            memberScopes.Add(scope, names);
        }

        var candidate = preferredName;
        for (var suffix = 1; IsReserved(names, candidate); suffix++)
            candidate = preferredName + suffix.ToString(CultureInfo.InvariantCulture);
        names.Add(candidate);
        allocated.Add(key, candidate);
        return candidate;
    }

    static bool IsReserved(HashSet<string> names, string name)
        => names.Contains(name) || names.Contains("get_" + name) || names.Contains("set_" + name);
}
