using System.Collections.Immutable;
using Plank.Reading;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record ParquetSchema
{
    static readonly ImmutableDictionary<string, IPageStrategy> EmptyPageStrategies =
        ImmutableDictionary.Create<string, IPageStrategy>(StringComparer.Ordinal);

    public ParquetSchema(ImmutableArray<Column> columns)
    {
        Columns = columns.IsDefault ? [] : columns;
        Definitions = NormalizeDefinitions(Columns);
        LeafPaths = BuildFlatLeafPaths(Columns);
        LeafProjectionInfos = BuildFlatLeafProjectionInfos(Columns);
    }

    public ParquetSchema(ImmutableArray<ColumnDefinition> definitions)
    {
        Definitions = definitions.IsDefault ? [] : definitions;
        if (TryProjectLeafColumns(Definitions, out var projectedColumns, out var projectedPaths, out var projectedInfos))
        {
            Columns = projectedColumns;
            LeafPaths = projectedPaths;
            LeafProjectionInfos = projectedInfos;
        }
        else
        {
            Columns = [];
            LeafPaths = [];
            LeafProjectionInfos = [];
        }
    }

    public ImmutableArray<Column> Columns { get; }

    public ImmutableArray<ColumnDefinition> Definitions { get; }

    public ImmutableDictionary<string, IPageStrategy> PageStrategiesByColumnName { get; init; } = EmptyPageStrategies;

    public ParquetReader CreateReader(Stream stream, ParquetReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ParquetReader.Open(new StreamReadSource(stream), this, options ?? ParquetReaderOptions.Default);
    }

    public ParquetReader CreateReader(IParquetReadSource source, ParquetReaderOptions? options = null)
        => ParquetReader.Open(source, this, options ?? ParquetReaderOptions.Default);

    public ParquetWriter CreateWriter(Stream stream, ParquetWriterOptions? options = null)
        => new(stream, this, options ?? ParquetWriterOptions.Default);

    internal ImmutableArray<ImmutableArray<string>> LeafPaths { get; }

    internal ImmutableArray<LeafProjectionInfo> LeafProjectionInfos { get; }

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
                LogicalType = column.LogicalType,
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

    static ImmutableArray<LeafProjectionInfo> BuildFlatLeafProjectionInfos(ImmutableArray<Column> columns)
    {
        if (columns.IsDefaultOrEmpty)
            return [];

        var builder = ImmutableArray.CreateBuilder<LeafProjectionInfo>(columns.Length);
        for (var i = 0; i < columns.Length; i++)
            builder.Add(new LeafProjectionInfo(IsList: false, ListOptional: false, ElementOptional: false,
                MaxRepetitionLevel: 0, MaxDefinitionLevel: columns[i].Options.Repetition == ParquetRepetition.Optional ? 1 : 0));
        return builder.MoveToImmutable();
    }

    static bool TryProjectLeafColumns(ImmutableArray<ColumnDefinition> definitions, out ImmutableArray<Column> columns,
        out ImmutableArray<ImmutableArray<string>> leafPaths, out ImmutableArray<LeafProjectionInfo> leafInfos)
    {
        if (definitions.IsDefaultOrEmpty)
        {
            columns = [];
            leafPaths = [];
            leafInfos = [];
            return true;
        }

        var columnsBuilder = ImmutableArray.CreateBuilder<Column>();
        var pathsBuilder = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var infosBuilder = ImmutableArray.CreateBuilder<LeafProjectionInfo>();
        var pathBuffer = new List<string>(8);
        for (var i = 0; i < definitions.Length; i++)
            if (!TryCollectLeaves(definitions[i], columnsBuilder, pathsBuilder, pathBuffer, repeatedLevel: 0,
                    definitionLevel: 0, infosBuilder, isListLeaf: false, listOptional: false, elementOptional: false))
            {
                columns = [];
                leafPaths = [];
                leafInfos = [];
                return false;
            }

        columns = columnsBuilder.ToImmutable();
        leafPaths = pathsBuilder.ToImmutable();
        leafInfos = infosBuilder.ToImmutable();
        return true;
    }

    static bool TryCollectLeaves(ColumnDefinition node, ImmutableArray<Column>.Builder columnsBuilder,
        ImmutableArray<ImmutableArray<string>>.Builder pathsBuilder, List<string> pathBuffer, int repeatedLevel,
        int definitionLevel, ImmutableArray<LeafProjectionInfo>.Builder infosBuilder, bool isListLeaf,
        bool listOptional, bool elementOptional)
    {
        pathBuffer.Add(node.Name);
        var nodeRepetition = node.Repetition == ParquetRepetition.Repeated;
        var nodeOptional = node.Repetition == ParquetRepetition.Optional;
        var nextRepeatedLevel = repeatedLevel + (nodeRepetition ? 1 : 0);
        var nextDefinitionLevel = definitionLevel + (nodeRepetition || nodeOptional ? 1 : 0);
        try
        {
            switch (node.Kind)
            {
                case NodeKind.Leaf:
                {
                    if (node.PhysicalType is null)
                        return false;
                    var repetition = repeatedLevel > 0
                        ? ParquetRepetition.Repeated
                        : nodeOptional ? ParquetRepetition.Optional : ParquetRepetition.Required;
                    var options = node.Options ?? ColumnOptions.Default;
                    if (options.Repetition != repetition)
                        options = new ColumnOptions(repetition, options.Encodings, options.TypeLength);
                    var path = pathBuffer.ToArray().ToImmutableArray();
                    var columnName = string.Join(".", path);
                    columnsBuilder.Add(new Column(columnName, node.PhysicalType.Value, options, node.LogicalType));
                    pathsBuilder.Add(path);
                    infosBuilder.Add(new LeafProjectionInfo(isListLeaf, listOptional, elementOptional,
                        MaxRepetitionLevel: nextRepeatedLevel, MaxDefinitionLevel: nextDefinitionLevel));
                    return true;
                }
                case NodeKind.Group:
                {
                    if (node.Children.IsDefaultOrEmpty)
                        return false;
                    for (var i = 0; i < node.Children.Length; i++)
                        if (!TryCollectLeaves(node.Children[i], columnsBuilder, pathsBuilder, pathBuffer, nextRepeatedLevel,
                                nextDefinitionLevel, infosBuilder, isListLeaf, listOptional, elementOptional))
                            return false;
                    return true;
                }
                case NodeKind.List:
                {
                    if (node.Children.Length != 1)
                        return false;

                    pathBuffer.Add("list");
                    var element = node.Children[0] with { Name = "element" };
                    try
                    {
                        return TryCollectLeaves(element, columnsBuilder, pathsBuilder, pathBuffer,
                            repeatedLevel: nextRepeatedLevel + 1, definitionLevel: nextDefinitionLevel + 1, infosBuilder,
                            isListLeaf: true, listOptional: listOptional || node.Repetition == ParquetRepetition.Optional,
                            elementOptional: element.Repetition == ParquetRepetition.Optional);
                    }
                    finally
                    {
                        pathBuffer.RemoveAt(pathBuffer.Count - 1);
                    }
                }
                case NodeKind.Map:
                {
                    if (node.Children.Length != 2)
                        return false;

                    var keyNode = node.Children[0];
                    var valueNode = node.Children[1];

                    pathBuffer.Add("key_value");
                    var keyOk = TryCollectLeaves(keyNode with { Name = "key" }, columnsBuilder, pathsBuilder, pathBuffer,
                        repeatedLevel: nextRepeatedLevel + 1, definitionLevel: nextDefinitionLevel + 1, infosBuilder, isListLeaf: true,
                        listOptional: node.Repetition == ParquetRepetition.Optional,
                        elementOptional: false);
                    if (!keyOk)
                    {
                        pathBuffer.RemoveAt(pathBuffer.Count - 1);
                        return false;
                    }

                    var valueOk = TryCollectLeaves(valueNode with { Name = "value" }, columnsBuilder, pathsBuilder, pathBuffer,
                        repeatedLevel: nextRepeatedLevel + 1, definitionLevel: nextDefinitionLevel + 1, infosBuilder,
                        isListLeaf: true, listOptional: node.Repetition == ParquetRepetition.Optional,
                        elementOptional: valueNode.Repetition == ParquetRepetition.Optional);
                    pathBuffer.RemoveAt(pathBuffer.Count - 1);
                    return valueOk;
                }
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
