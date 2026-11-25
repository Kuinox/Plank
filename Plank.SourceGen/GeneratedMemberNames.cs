using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Plank.SourceGen;

/// <summary>Allocates generated identifiers without changing the names of schema properties.</summary>
sealed class GeneratedMemberNames
{
    static readonly string[] ApiNames =
    [
        "Schema", "Writer", "Reader", "DatasetWriter", "Route", "Row", "ReadRow", "Projection",
        "RowCursor", "RowReader", "ReadRowGroup", "ReadRowGroupCollection", "SchemaWriter", "RowGroup",
        "PipelineWriter", "BufferSlot", "CreateDatasetWriter", "CreateRowWriter", "CreateRowReader",
        "CreateReader", "CreateWriter", "s_rowApiColumns", "GeneratedNestedToUnixMicroseconds"
    ];

    readonly Dictionary<string, string> apiNames = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> helperNames = new(StringComparer.Ordinal);

    readonly HashSet<string> reserved;
    readonly HashSet<string> propertyNames;
    readonly Dictionary<string, HashSet<string>> memberScopes = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> allocated = new(StringComparer.Ordinal);

    internal GeneratedMemberNames(INamedTypeSymbol schemaType)
    {
        reserved = new HashSet<string>(StringComparer.Ordinal) { schemaType.Name };
        propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in schemaType.GetMembers())
        {
            reserved.Add(member.Name);
            if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, IsImplicitlyDeclared: false })
                propertyNames.Add(member.Name);
        }
        // Allocate stable API names before property-derived helpers can claim one of them.
        foreach (var name in ApiNames)
            Root(name);
    }

    internal string Root(string name) => GetRootName(apiNames, name);

    internal string Helper(string name) => GetRootName(helperNames, name);

    string GetRootName(Dictionary<string, string> cache, string name)
    {
        if (!cache.TryGetValue(name, out var result))
        {
            result = Reserve(reserved, name);
            cache.Add(name, result);
        }
        return result;
    }

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

        var candidate = Reserve(names, preferredName);
        allocated.Add(key, candidate);
        return candidate;
    }

    static string Reserve(HashSet<string> names, string preferredName)
    {
        var candidate = preferredName;
        for (var suffix = 1; IsReserved(names, candidate); suffix++)
            candidate = preferredName + suffix.ToString(CultureInfo.InvariantCulture);
        names.Add(candidate);
        return candidate;
    }

    static bool IsReserved(HashSet<string> names, string name)
        => names.Contains(name) || names.Contains("get_" + name) || names.Contains("set_" + name);
}
