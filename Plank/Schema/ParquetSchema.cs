using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ParquetSchema
{
    public ParquetSchema(ImmutableArray<Column> columns)
    {
        Columns = columns.IsDefault ? [] : columns;
        Definitions = NormalizeDefinitions(Columns);
        LeafPaths = BuildFlatLeafPaths(Columns);
    }

    public ParquetSchema(ImmutableArray<ColumnDefinition> definitions)
    {
        Definitions = definitions.IsDefault ? [] : definitions;
        if (TryProjectLeafColumns(Definitions, out var projectedColumns, out var projectedPaths))
        {
            Columns = projectedColumns;
            LeafPaths = projectedPaths;
        }
        else
        {
            Columns = [];
            LeafPaths = [];
        }
    }

    public ImmutableArray<Column> Columns { get; }

    public ImmutableArray<ColumnDefinition> Definitions { get; }

    internal ImmutableArray<ImmutableArray<string>> LeafPaths { get; }

    public void Validate()
    {
        ValidateDefinitions();

        if (Columns.IsDefaultOrEmpty)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in Columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            column.Validate();
            if (!seen.Add(column.Name))
                throw new InvalidOperationException($"Duplicate column name '{column.Name}' is not allowed.");
        }
    }

    void ValidateDefinitions()
    {
        if (Definitions.IsDefaultOrEmpty)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Definitions.Length; i++)
        {
            var definition = Definitions[i];
            ArgumentNullException.ThrowIfNull(definition);
            definition.Validate();
            if (!seen.Add(definition.Name))
                throw new InvalidOperationException($"Duplicate root node name '{definition.Name}' is not allowed.");
        }
    }

    static ImmutableArray<ColumnDefinition> NormalizeDefinitions(ImmutableArray<Column> columns)
    {
        if (columns.IsDefaultOrEmpty)
            return [];

        var builder = ImmutableArray.CreateBuilder<ColumnDefinition>(columns.Length);
        foreach (var column in columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            builder.Add(new ColumnDefinition
            {
                Name = column.Name,
                Kind = NodeKind.Leaf,
                Repetition = column.Options.Repetition == ParquetRepetition.Unspecified
                    ? ParquetRepetition.Required
                    : column.Options.Repetition,
                PhysicalType = column.PhysicalType,
                Options = column.Options
            });
        }

        return builder.MoveToImmutable();
    }

    static ImmutableArray<ImmutableArray<string>> BuildFlatLeafPaths(ImmutableArray<Column> columns)
    {
        if (columns.IsDefaultOrEmpty)
            return [];

        var builder = ImmutableArray.CreateBuilder<ImmutableArray<string>>(columns.Length);
        for (var i = 0; i < columns.Length; i++)
            builder.Add([columns[i].Name]);
        return builder.MoveToImmutable();
    }

    static bool TryProjectLeafColumns(ImmutableArray<ColumnDefinition> definitions, out ImmutableArray<Column> columns,
        out ImmutableArray<ImmutableArray<string>> leafPaths)
    {
        if (definitions.IsDefaultOrEmpty)
        {
            columns = [];
            leafPaths = [];
            return true;
        }

        var columnsBuilder = ImmutableArray.CreateBuilder<Column>();
        var pathsBuilder = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var pathBuffer = new List<string>(8);
        for (var i = 0; i < definitions.Length; i++)
            if (!TryCollectLeaves(definitions[i], columnsBuilder, pathsBuilder, pathBuffer, hasRepeatedAncestor: false,
                    hasOptionalAncestor: false))
            {
                columns = [];
                leafPaths = [];
                return false;
            }

        columns = columnsBuilder.ToImmutable();
        leafPaths = pathsBuilder.ToImmutable();
        return true;
    }

    static bool TryCollectLeaves(ColumnDefinition node, ImmutableArray<Column>.Builder columnsBuilder,
        ImmutableArray<ImmutableArray<string>>.Builder pathsBuilder, List<string> pathBuffer, bool hasRepeatedAncestor,
        bool hasOptionalAncestor)
    {
        pathBuffer.Add(node.Name);
        var nodeOptional = node.Repetition == ParquetRepetition.Optional;
        var optionalChain = hasOptionalAncestor || nodeOptional;
        try
        {
            switch (node.Kind)
            {
                case NodeKind.Leaf:
                {
                    if (node.PhysicalType is null)
                        return false;
                    var repetition = hasRepeatedAncestor
                        ? ParquetRepetition.Repeated
                        : optionalChain ? ParquetRepetition.Optional : ParquetRepetition.Required;
                    var options = node.Options ?? ColumnOptions.Default;
                    if (options.Repetition != repetition)
                        options = new ColumnOptions(repetition, options.Encodings, options.TypeLength);
                    var path = pathBuffer.ToArray().ToImmutableArray();
                    var columnName = string.Join(".", path);
                    columnsBuilder.Add(new Column(columnName, node.PhysicalType.Value, options));
                    pathsBuilder.Add(path);
                    return true;
                }
                case NodeKind.Group:
                {
                    if (node.Children.IsDefaultOrEmpty)
                        return false;
                    for (var i = 0; i < node.Children.Length; i++)
                        if (!TryCollectLeaves(node.Children[i], columnsBuilder, pathsBuilder, pathBuffer, hasRepeatedAncestor,
                                optionalChain))
                            return false;
                    return true;
                }
                case NodeKind.List:
                {
                    if (node.Children.Length != 1)
                        return false;

                    pathBuffer.Add("list");
                    var element = node.Children[0];
                    pathBuffer.Add(element.Name);
                    try
                    {
                        if (element.Kind != NodeKind.Leaf || element.PhysicalType is null)
                            return false;
                        var repetition = ParquetRepetition.Repeated;
                        var options = element.Options ?? ColumnOptions.Default;
                        if (options.Repetition != repetition)
                            options = new ColumnOptions(repetition, options.Encodings, options.TypeLength);
                        var path = pathBuffer.ToArray().ToImmutableArray();
                        var columnName = string.Join(".", path);
                        columnsBuilder.Add(new Column(columnName, element.PhysicalType.Value, options));
                        pathsBuilder.Add(path);
                        return true;
                    }
                    finally
                    {
                        pathBuffer.RemoveAt(pathBuffer.Count - 1);
                        pathBuffer.RemoveAt(pathBuffer.Count - 1);
                    }
                }
                case NodeKind.Map:
                    return false;
                default:
                    return false;
            }
        }
        finally
        {
            pathBuffer.RemoveAt(pathBuffer.Count - 1);
        }
    }
}
