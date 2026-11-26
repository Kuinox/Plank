using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Writing;

namespace Plank.Schema;

public sealed record ParquetSchema
{
    public ParquetSchema(ImmutableArray<ColumnDefinition> definitions)
        : this(definitions, ProjectOrThrow(definitions))
    {
    }

    ParquetSchema(ImmutableArray<ColumnDefinition> definitions,
        (ImmutableArray<Column> Columns, ImmutableArray<ImmutableArray<string>> Paths,
            ImmutableArray<LeafProjectionInfo> Infos) projection)
    {
        Definitions = definitions.IsDefault ? [] : definitions;
        Columns = projection.Columns;
        LeafColumns = BuildLeafColumns(projection.Columns, projection.Infos);
        LeafPaths = projection.Paths;
        LeafProjectionInfos = projection.Infos;
    }

    static (ImmutableArray<Column> Columns, ImmutableArray<ImmutableArray<string>> Paths,
        ImmutableArray<LeafProjectionInfo> Infos) ProjectOrThrow(ImmutableArray<ColumnDefinition> definitions)
    {
        if (!TryProjectLeafColumns(definitions, out var columns, out var paths, out var infos, out var error))
            throw error is { } reason
                ? new ArgumentException(reason.Message, reason.ParameterName)
                : new ArgumentException("Schema definitions must form a valid, projectable schema.", nameof(definitions));
        return (columns, paths, infos);
    }

    internal bool NestedShapeMatches(int ordinal, ParquetSchema other, int otherOrdinal)
    {
        var left = LeafPaths[ordinal];
        var right = other.LeafPaths[otherOrdinal];
        if (left.Length != right.Length)
            return false;
        var a = Find(Definitions, left[0]);
        var b = Find(other.Definitions, right[0]);
        for (var i = 0; a is not null && b is not null;)
        {
            if (a.Kind != b.Kind || Normalize(a.Repetition) != Normalize(b.Repetition))
                return false;
            if (a.Kind == NodeKind.Leaf)
                return i == left.Length - 1;
            i += a.Kind is NodeKind.Map or NodeKind.List ? 2 : 1;
            if (i >= left.Length)
                return false;
            a = Child(a, left[i]);
            b = Child(b, right[i]);
        }
        return false;

        static ParquetRepetition Normalize(ParquetRepetition value)
            => value == ParquetRepetition.Unspecified ? ParquetRepetition.Required : value;

        static ColumnDefinition? Child(ColumnDefinition node, string name)
            => node.Kind switch
            {
                NodeKind.List => node.Children[0],
                NodeKind.Map => node.Children[name == "key" ? 0 : 1],
                _ => Find(node.Children, name)
            };

        static ColumnDefinition? Find(ImmutableArray<ColumnDefinition> nodes, string name)
        {
            foreach (var node in nodes)
                if (node.Name == name)
                    return node;
            return null;
        }
    }

    /// <summary>
    /// Builds a schema, returning <see langword="false"/> instead of throwing when the definitions do not project.
    /// </summary>
    /// <remarks>
    /// For the reader, definitions that do not project mean the file's footer is malformed, not that a caller passed
    /// bad arguments — so it needs the failure as a value it can turn into <see cref="CorruptParquetException"/>.
    /// </remarks>
    internal static bool TryCreate(ImmutableArray<ColumnDefinition> definitions,
        [NotNullWhen(true)] out ParquetSchema? schema, out string? error)
    {
        if (!TryProjectLeafColumns(definitions, out var columns, out var paths, out var infos, out var reason))
        {
            schema = null;
            error = reason?.Message;
            return false;
        }

        error = null;

        schema = new ParquetSchema(definitions, (columns, paths, infos));
        return true;
    }

    public ImmutableArray<ColumnDefinition> Definitions { get; }

    public ImmutableArray<LeafColumn> LeafColumns { get; }

    public ParquetReader CreateReader(Stream stream, ParquetReaderOptions? options = null,
        ParquetPagePruner? pagePruner = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new ParquetReader(this, options);
        try
        {
            reader.Reset(stream, pagePruner);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public ParquetReader CreateReader(IParquetReadSource source, ParquetReaderOptions? options = null,
        ParquetPagePruner? pagePruner = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reader = new ParquetReader(this, options);
        try
        {
            reader.Reset(source, pagePruner);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public ParquetWriter CreateWriter(Stream stream, ParquetWriterOptions? options = null)
        => new(stream, this, options ?? ParquetWriterOptions.Default);

    public ParquetWriter CreateWriter(IParquetWriteSource destination, ParquetWriterOptions? options = null)
        => new(destination, this, options ?? ParquetWriterOptions.Default);

    /// <summary>Appends to a file whose complete physical schema matches this schema.</summary>
    /// <remarks>Field order, nested layout, repetition, logical types, and field IDs must match. Reader
    /// projections and schema evolution are not supported when retaining existing encoded pages.</remarks>
    public ParquetWriter CreateAppender(Stream stream, ParquetAppendOptions? options = null)
        => new(stream, this, options ?? ParquetAppendOptions.Default);

    /// <summary>Appends to an existing file, copying its retained data if a separate destination is used.</summary>
    /// <remarks>The complete physical schema must match. The source must remain stable while opening the
    /// appender. Source and destination may refer to the same storage.</remarks>
    public ParquetWriter CreateAppender(IParquetReadSource source, IParquetWriteSource destination,
        ParquetAppendOptions? options = null)
        => new(source, destination, this, options ?? ParquetAppendOptions.Default);

    /// <summary>Opens a file for in-place merging with an exactly matching physical schema.</summary>
    public ParquetFileMerger CreateMerger(IParquetReadWriteSource destination,
        ParquetMergeOptions? options = null)
        => new(destination, this, options ?? ParquetMergeOptions.Default);

    /// <summary>Copies a file into a destination for merging with an exactly matching physical schema.</summary>
    /// <remarks>Source and destination must refer to different storage, including when using separate
    /// custom adapters. The source must remain stable throughout the merge.</remarks>
    public ParquetFileMerger CreateMerger(IParquetReadSource source, IParquetWriteSource destination,
        ParquetMergeOptions? options = null)
        => new(source, destination, this, options ?? ParquetMergeOptions.Default);

    internal ImmutableArray<ImmutableArray<string>> LeafPaths { get; }

    internal ImmutableArray<LeafProjectionInfo> LeafProjectionInfos { get; }

    static ImmutableArray<LeafColumn> BuildLeafColumns(ImmutableArray<Column> columns,
        ImmutableArray<LeafProjectionInfo> projectionInfos)
    {
        if (columns.IsDefaultOrEmpty)
            return [];

        var builder = ImmutableArray.CreateBuilder<LeafColumn>(columns.Length);
        for (var i = 0; i < columns.Length; i++)
            builder.Add(new LeafColumn(columns[i], i, projectionInfos[i]));
        return builder.MoveToImmutable();
    }

    internal ImmutableArray<Column> Columns { get; }

    // Carries the reason a projection failed back out of the recursion, so the caller can decide whether it means
    // "you passed bad arguments" (public constructor) or "this file is malformed" (reader).
    sealed class ProjectionError
    {
        internal (string Message, string ParameterName)? Value;
    }

    static bool TryProjectLeafColumns(ImmutableArray<ColumnDefinition> definitions, out ImmutableArray<Column> columns,
        out ImmutableArray<ImmutableArray<string>> leafPaths, out ImmutableArray<LeafProjectionInfo> leafInfos,
        out (string Message, string ParameterName)? error)
    {
        error = null;
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
        var mapProjections = new List<MapProjectionInfo>(2);
        var repeatedDefinitionLevels = new List<int>(2);
        var nextMapId = 0;
        var errorSink = new ProjectionError();
        for (var i = 0; i < definitions.Length; i++)
            if (!TryCollectLeaves(definitions[i], columnsBuilder, pathsBuilder, pathBuffer, repeatedLevel: 0,
                    definitionLevel: 0, infosBuilder, isListLeaf: false, listOptional: false, elementOptional: false,
                    mapProjections, repeatedDefinitionLevels, ref nextMapId, errorSink))
            {
                columns = [];
                leafPaths = [];
                leafInfos = [];
                error = errorSink.Value;
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
        bool listOptional, bool elementOptional, List<MapProjectionInfo> mapProjections,
        List<int> repeatedDefinitionLevels, ref int nextMapId, ProjectionError error)
    {
        pathBuffer.Add(node.Name);
        var nodeRepetition = node.Repetition == ParquetRepetition.Repeated;
        var nodeOptional = node.Repetition == ParquetRepetition.Optional;
        var nextRepeatedLevel = repeatedLevel + (nodeRepetition ? 1 : 0);
        var nextDefinitionLevel = definitionLevel + (nodeRepetition || nodeOptional ? 1 : 0);
        if (nodeRepetition)
            repeatedDefinitionLevels.Add(nextDefinitionLevel);
        try
        {
            switch (node.Kind)
            {
                case NodeKind.Leaf:
                {
                    if (node.PhysicalType is null)
                        return false;
                    var repetition = nextRepeatedLevel > 0
                        ? ParquetRepetition.Repeated
                        : nextDefinitionLevel > 0 ? ParquetRepetition.Optional : ParquetRepetition.Required;
                    var options = (node.Options ?? ColumnOptions.Default).WithRepetition(repetition);
                    var path = ImmutableArray.Create<string>(CollectionsMarshal.AsSpan(pathBuffer));
                    var columnName = string.Join(".", path);
                    // Every leaf in the schema is constructed here, so this is the one place that has to decide
                    // whether the logical/physical pair is acceptable. Reporting it as a value rather than letting
                    // Column's constructor throw is what lets the reader — which projects schemas out of untrusted
                    // file footers — turn it into a CorruptParquetException instead of an ArgumentException.
                    error.Value = ColumnDefinition.DescribeLogicalTypeError(columnName, node.PhysicalType.Value,
                        options, node.LogicalType);
                    if (error.Value is not null)
                        return false;
                    columnsBuilder.Add(new Column(columnName, node.PhysicalType.Value, options, node.LogicalType,
                        node.PageStrategy, node.Converter, node.FieldId));
                    pathsBuilder.Add(path);
                    infosBuilder.Add(new LeafProjectionInfo(isListLeaf, listOptional, elementOptional,
                        MaxRepetitionLevel: nextRepeatedLevel, MaxDefinitionLevel: nextDefinitionLevel,
                        MapProjections: mapProjections.ToImmutableArray(),
                        RepeatedDefinitionLevels: repeatedDefinitionLevels.ToImmutableArray()));
                    return true;
                }
                case NodeKind.Group:
                {
                    if (node.Children.IsDefaultOrEmpty)
                        return false;
                    error.Value = ColumnDefinition.DescribeGroupLogicalTypeError(node.Name, node.LogicalType,
                        node.Children.AsSpan());
                    if (error.Value is not null)
                        return false;
                    for (var i = 0; i < node.Children.Length; i++)
                        if (!TryCollectLeaves(node.Children[i], columnsBuilder, pathsBuilder, pathBuffer, nextRepeatedLevel,
                                nextDefinitionLevel, infosBuilder, isListLeaf, listOptional, elementOptional,
                                mapProjections, repeatedDefinitionLevels, ref nextMapId, error))
                            return false;
                    return true;
                }
                case NodeKind.List:
                {
                    if (node.Children.Length != 1)
                        return false;

                    pathBuffer.Add("list");
                    repeatedDefinitionLevels.Add(nextDefinitionLevel + 1);
                    var element = node.Children[0] with { Name = "element" };
                    try
                    {
                        return TryCollectLeaves(element, columnsBuilder, pathsBuilder, pathBuffer,
                            repeatedLevel: nextRepeatedLevel + 1, definitionLevel: nextDefinitionLevel + 1, infosBuilder,
                            isListLeaf: true, listOptional: listOptional || node.Repetition == ParquetRepetition.Optional,
                            elementOptional: element.Repetition == ParquetRepetition.Optional, mapProjections,
                            repeatedDefinitionLevels, ref nextMapId, error);
                    }
                    finally
                    {
                        pathBuffer.RemoveAt(pathBuffer.Count - 1);
                        repeatedDefinitionLevels.RemoveAt(repeatedDefinitionLevels.Count - 1);
                    }
                }
                case NodeKind.Map:
                {
                    if (node.Children.Length is < 1 or > 2)
                        return false;

                    var keyNode = node.Children[0];
                    var mapId = nextMapId++;
                    var mapRepetitionLevel = nextRepeatedLevel + 1;

                    pathBuffer.Add("key_value");
                    repeatedDefinitionLevels.Add(nextDefinitionLevel + 1);
                    mapProjections.Add(new MapProjectionInfo(mapId, mapRepetitionLevel, pathBuffer.Count, IsKey: true));
                    var keyOk = TryCollectLeaves(keyNode with { Name = "key" }, columnsBuilder, pathsBuilder,
                        pathBuffer, repeatedLevel: mapRepetitionLevel, definitionLevel: nextDefinitionLevel + 1,
                        infosBuilder, isListLeaf: true, listOptional: node.Repetition == ParquetRepetition.Optional,
                        elementOptional: false, mapProjections, repeatedDefinitionLevels, ref nextMapId, error);
                    mapProjections.RemoveAt(mapProjections.Count - 1);
                    if (!keyOk || node.Children.Length == 1)
                    {
                        pathBuffer.RemoveAt(pathBuffer.Count - 1);
                        repeatedDefinitionLevels.RemoveAt(repeatedDefinitionLevels.Count - 1);
                        return keyOk;
                    }

                    var valueNode = node.Children[1];
                    mapProjections.Add(new MapProjectionInfo(mapId, mapRepetitionLevel, pathBuffer.Count, IsKey: false));
                    var valueOk = TryCollectLeaves(valueNode with { Name = "value" }, columnsBuilder, pathsBuilder,
                        pathBuffer, repeatedLevel: mapRepetitionLevel, definitionLevel: nextDefinitionLevel + 1, infosBuilder,
                        isListLeaf: true, listOptional: node.Repetition == ParquetRepetition.Optional,
                        elementOptional: valueNode.Repetition == ParquetRepetition.Optional, mapProjections,
                        repeatedDefinitionLevels, ref nextMapId, error);
                    mapProjections.RemoveAt(mapProjections.Count - 1);
                    pathBuffer.RemoveAt(pathBuffer.Count - 1);
                    repeatedDefinitionLevels.RemoveAt(repeatedDefinitionLevels.Count - 1);
                    return valueOk;
                }
                default:
                    return false;
            }
        }
        finally
        {
            pathBuffer.RemoveAt(pathBuffer.Count - 1);
            if (nodeRepetition)
                repeatedDefinitionLevels.RemoveAt(repeatedDefinitionLevels.Count - 1);
        }
    }
}
