using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Plank.Reading.Physical;
using Plank.Schema;
using PhysicalFileMetadata = Plank.Reading.Physical.ParquetFileMetadata;

namespace Plank.Reading.Logical.Internal;

static class PhysicalSchemaBinder
{
    internal static ParquetSchema BuildSchema(PhysicalFileMetadata metadata)
    {
        if (metadata.SchemaNodeCount == 0)
            throw new CorruptParquetException("File metadata is missing schema.");

        var root = metadata.SchemaNodes[0];
        var definitions = ImmutableArray.CreateBuilder<ColumnDefinition>(root.ChildCount);
        var index = 1;
        for (var i = 0; i < root.ChildCount; i++)
            definitions.Add(BuildDefinition(metadata, ref index));

        if (index != metadata.SchemaNodeCount)
            throw new CorruptParquetException("Parquet schema contains unreferenced nodes.");

        return new ParquetSchema(definitions.MoveToImmutable());
    }

    internal static InternalParquetFooter Bind(ParquetFileReader physicalReader, ParquetSchema fileSchema,
        ParquetSchema requestedSchema, InternalParquetFooter previous, bool strict, IParquetBufferPool bufferPool,
        int footerVersion)
    {
        var requestedColumns = requestedSchema.Columns;
        ParquetBuffer rentedOrdinals = default;
        Span<int> projectedOrdinals = requestedColumns.Length <= 256
            ? stackalloc int[requestedColumns.Length]
            : ParquetBuffer.AsSpan<int>(
                rentedOrdinals = bufferPool.Rent(checked((uint)(requestedColumns.Length * sizeof(int)))),
                requestedColumns.Length);
        try
        {
            var metadata = physicalReader.Metadata;
            if (strict)
                BuildStrictProjection(metadata, fileSchema, requestedSchema, projectedOrdinals, bufferPool);
            else
                for (var i = 0; i < projectedOrdinals.Length; i++)
                    projectedOrdinals[i] = i < metadata.ColumnCount ? i : -1;

            var rowGroupCount = metadata.RowGroupCount;
            var rowGroups = previous.RowGroups.Length == rowGroupCount
                ? previous.RowGroups
                : new InternalRowGroupMetadata[rowGroupCount];
            for (var rowGroupOrdinal = 0; rowGroupOrdinal < rowGroupCount; rowGroupOrdinal++)
            {
                var physicalRowGroup = metadata.RowGroup(rowGroupOrdinal);
                var columnCount = strict ? requestedColumns.Length : physicalRowGroup.ColumnCount;
                var previousColumns = rowGroups[rowGroupOrdinal].Columns ?? [];
                var columns = previousColumns.Length == columnCount
                    ? previousColumns
                    : new InternalColumnChunkMetadata[columnCount];

                for (var columnOrdinal = 0; columnOrdinal < columnCount; columnOrdinal++)
                {
                    var fileOrdinal = strict
                        ? projectedOrdinals[columnOrdinal]
                        : columnOrdinal;
                    if (fileOrdinal >= physicalRowGroup.ColumnCount)
                        throw new CorruptParquetException(
                            $"Row group {rowGroupOrdinal} contains {physicalRowGroup.ColumnCount} columns, but file schema column {fileOrdinal} was requested.");

                    var physicalChunk = metadata.ColumnChunk(rowGroupOrdinal, fileOrdinal);
                    var path = strict && columnOrdinal < requestedColumns.Length
                        ? requestedColumns[columnOrdinal].Name
                        : BuildColumnPath(metadata, metadata.ColumnSchema(fileOrdinal));
                    var encodings = ReuseEncodings(columns[columnOrdinal].Encodings, physicalChunk);
                    columns[columnOrdinal] = new InternalColumnChunkMetadata(physicalChunk, encodings, path);
                }

                rowGroups[rowGroupOrdinal] = new InternalRowGroupMetadata(rowGroupOrdinal, physicalRowGroup.MetadataOffset,
                    physicalRowGroup.ColumnChunkOffset, physicalRowGroup.RowCount, columns, footerVersion);
            }

            return new InternalParquetFooter(metadata.FileVersion, rowGroups);
        }
        finally
        {
            rentedOrdinals.Dispose();
        }
    }

    static ColumnDefinition BuildDefinition(PhysicalFileMetadata metadata, ref int index)
    {
        if ((uint)index >= (uint)metadata.SchemaNodeCount)
            throw new CorruptParquetException("Parquet schema child count exceeds schema node count.");

        var node = metadata.SchemaNodes[index++];
        var name = Encoding.UTF8.GetString(metadata.SchemaNodeNameUtf8(node.Ordinal));
        if (node.PhysicalType is { } physicalType)
            return new ColumnDefinition
            {
                Name = name,
                Kind = NodeKind.Leaf,
                Repetition = node.Repetition,
                PhysicalType = physicalType,
                LogicalType = ConvertLogicalType(node.LogicalType),
                Options = new ColumnOptions(node.Repetition, typeLength: node.TypeLength),
                Children = []
            };

        return node.Kind switch
        {
            NodeKind.List => BuildListDefinition(metadata, ref index, node, name),
            NodeKind.Map => BuildMapDefinition(metadata, ref index, node, name),
            _ => BuildGroupDefinition(metadata, ref index, node, name)
        };
    }

    static ColumnDefinition BuildGroupDefinition(PhysicalFileMetadata metadata, ref int index,
        ParquetSchemaNodeInfo node, string name)
    {
        var children = ImmutableArray.CreateBuilder<ColumnDefinition>(node.ChildCount);
        for (var i = 0; i < node.ChildCount; i++)
            children.Add(BuildDefinition(metadata, ref index));
        return new ColumnDefinition
        {
            Name = name,
            Kind = NodeKind.Group,
            Repetition = node.Repetition,
            Children = children.MoveToImmutable()
        };
    }

    static ColumnDefinition BuildListDefinition(PhysicalFileMetadata metadata, ref int index,
        ParquetSchemaNodeInfo node, string name)
    {
        if (node.ChildCount != 1)
            throw new CorruptParquetException($"LIST schema node '{name}' must contain exactly one repeated list child.");
        if ((uint)index >= (uint)metadata.SchemaNodeCount)
            throw new CorruptParquetException($"LIST schema node '{name}' is missing its repeated list child.");

        var repeated = metadata.SchemaNodes[index++];
        if (repeated.ChildCount != 1)
            throw new CorruptParquetException($"LIST schema node '{name}' repeated child must contain exactly one element.");

        var element = BuildDefinition(metadata, ref index) with { Name = "element" };
        return new ColumnDefinition
        {
            Name = name,
            Kind = NodeKind.List,
            Repetition = node.Repetition,
            Children = [element]
        };
    }

    static ColumnDefinition BuildMapDefinition(PhysicalFileMetadata metadata, ref int index,
        ParquetSchemaNodeInfo node, string name)
    {
        if (node.Repetition is not (ParquetRepetition.Required or ParquetRepetition.Optional))
            throw new CorruptParquetException($"MAP schema node '{name}' must be required or optional.");
        if (node.ChildCount != 1)
            throw new CorruptParquetException($"MAP schema node '{name}' must contain exactly one key_value child.");
        if ((uint)index >= (uint)metadata.SchemaNodeCount)
            throw new CorruptParquetException($"MAP schema node '{name}' is missing its key_value child.");

        var keyValue = metadata.SchemaNodes[index++];
        if (keyValue.PhysicalType is not null)
            throw new CorruptParquetException($"MAP schema node '{name}' key_value child must be a group.");
        if (keyValue.Repetition != ParquetRepetition.Repeated)
            throw new CorruptParquetException($"MAP schema node '{name}' key_value child must be repeated.");
        if (keyValue.ChildCount is < 1 or > 2)
            throw new CorruptParquetException($"MAP schema node '{name}' key_value child must contain a key and optional value.");

        var key = BuildDefinition(metadata, ref index) with { Name = "key" };
        if (key.Repetition != ParquetRepetition.Required)
            throw new CorruptParquetException($"MAP schema node '{name}' key must be required.");
        ImmutableArray<ColumnDefinition> children;
        if (keyValue.ChildCount == 1)
            children = ImmutableArray.Create(key);
        else
        {
            var value = BuildDefinition(metadata, ref index) with { Name = "value" };
            if (value.Repetition is not (ParquetRepetition.Required or ParquetRepetition.Optional))
                throw new CorruptParquetException($"MAP schema node '{name}' value must be required or optional.");
            children = ImmutableArray.Create(key, value);
        }
        return new ColumnDefinition
        {
            Name = name,
            Kind = NodeKind.Map,
            Repetition = node.Repetition,
            Children = children
        };
    }

    static void BuildStrictProjection(PhysicalFileMetadata metadata, ParquetSchema fileSchema,
        ParquetSchema requestedSchema, Span<int> projectedOrdinals, IParquetBufferPool bufferPool)
    {
        var requestedColumns = requestedSchema.Columns;
        for (var requestedOrdinal = 0; requestedOrdinal < requestedColumns.Length; requestedOrdinal++)
        {
            var requested = requestedColumns[requestedOrdinal];
            var requestedPath = requestedSchema.LeafPaths[requestedOrdinal];
            var match = -1;
            for (var fileOrdinal = 0; fileOrdinal < metadata.ColumnCount; fileOrdinal++)
            {
                var fileColumn = metadata.ColumnSchema(fileOrdinal);
                if (!PathEquals(metadata, fileColumn, requestedPath, bufferPool))
                    continue;
                if (match >= 0)
                    throw new CorruptParquetException(
                        $"File schema contains duplicate column path '{requested.Name}'.");
                match = fileOrdinal;
            }

            if (match < 0)
                for (var fileOrdinal = 0; fileOrdinal < fileSchema.Columns.Length; fileOrdinal++)
                {
                    if (!PathEquals(fileSchema.LeafPaths[fileOrdinal], requestedPath))
                        continue;
                    if (match >= 0)
                        throw new CorruptParquetException(
                            $"File schema contains duplicate column path '{requested.Name}'.");
                    match = fileOrdinal;
                }

            if (match < 0)
                throw new InvalidOperationException(
                    $"Requested schema column '{requested.Name}' is not present in the file schema.");

            var physicalType = metadata.ColumnSchema(match).PhysicalType;
            if (requested.PhysicalType != physicalType)
                throw new InvalidOperationException(
                    $"Requested schema column '{requested.Name}' has physical type {requested.PhysicalType}, but file schema has {physicalType}.");
            projectedOrdinals[requestedOrdinal] = match;
        }
    }

    static string BuildColumnPath(PhysicalFileMetadata metadata, ParquetColumnSchemaInfo column)
    {
        if (column.PathSegmentCount == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (var segmentOrdinal = 0; segmentOrdinal < column.PathSegmentCount; segmentOrdinal++)
        {
            if (segmentOrdinal > 0)
                builder.Append('.');
            builder.Append(Encoding.UTF8.GetString(metadata.ColumnPathSegmentUtf8(column.Ordinal, segmentOrdinal)));
        }

        return builder.ToString();
    }

    static LogicalType? ConvertLogicalType(LogicalTypeInfo logicalType)
        => logicalType.Kind switch
        {
            LogicalTypeKind.None => null,
            LogicalTypeKind.String => new LogicalType.String(),
            LogicalTypeKind.Json => new LogicalType.Json(),
            LogicalTypeKind.Uuid => new LogicalType.Uuid(),
            LogicalTypeKind.Date => new LogicalType.Date(),
            LogicalTypeKind.Time => new LogicalType.Time(logicalType.Unit, logicalType.IsAdjustedToUtc),
            LogicalTypeKind.Timestamp => new LogicalType.Timestamp(logicalType.Unit, logicalType.IsAdjustedToUtc),
            LogicalTypeKind.Integer => new LogicalType.Int(logicalType.BitWidth, logicalType.IsSigned),
            LogicalTypeKind.Decimal => new LogicalType.Decimal(logicalType.Precision, logicalType.Scale),
            _ => throw new NotSupportedException($"Logical type '{logicalType.Kind}' is not supported.")
        };

    static bool PathEquals(PhysicalFileMetadata metadata, ParquetColumnSchemaInfo column,
        ImmutableArray<string> requestedPath, IParquetBufferPool bufferPool)
    {
        if (column.PathSegmentCount != requestedPath.Length)
            return false;

        var byteCount = 0;
        for (var i = 0; i < requestedPath.Length; i++)
            byteCount = Math.Max(byteCount, Encoding.UTF8.GetByteCount(requestedPath[i]));

        ParquetBuffer rented = default;
        Span<byte> requestedBytes = byteCount <= 1024
            ? stackalloc byte[byteCount]
            : (rented = bufferPool.Rent(checked((uint)byteCount))).Span[..byteCount];
        try
        {
            for (var segmentOrdinal = 0; segmentOrdinal < column.PathSegmentCount; segmentOrdinal++)
            {
                var requestedLength = Encoding.UTF8.GetBytes(requestedPath[segmentOrdinal], requestedBytes);
                var segment = metadata.ColumnPathSegmentUtf8(column.Ordinal, segmentOrdinal);
                if (!segment.SequenceEqual(requestedBytes[..requestedLength]))
                    return false;
            }
            return true;
        }
        finally
        {
            rented.Dispose();
        }
    }

    static bool PathEquals(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    static EncodingKind[] ReuseEncodings(EncodingKind[]? previous, ParquetColumnChunkInfo chunk)
    {
        var chunkEncodings = chunk.Encodings;
        var encodings = previous is not null && previous.Length == chunkEncodings.Count
            ? previous
            : new EncodingKind[chunkEncodings.Count];
        chunkEncodings.CopyTo(encodings);
        return encodings;
    }
}
