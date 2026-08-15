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

        // Projection is where every leaf and group in the schema is validated, so it is also the only place that can
        // tell us the footer describes something that is not a schema — an incompatible logical/physical pair, a
        // repeated node with no child, a map without its key. The public constructor reports those as argument
        // mistakes, which they are for a caller building a schema by hand but not for a file we were handed.
        if (!ParquetSchema.TryCreate(definitions.MoveToImmutable(), out var schema, out var error))
            throw new CorruptParquetException(error is null
                ? "Parquet schema definitions do not form a valid, projectable schema."
                : $"Parquet schema is not valid: {error}");

        return schema;
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
        {
            var logicalType = ConvertLogicalType(metadata, node);
            var options = new ColumnOptions(node.Repetition, typeLength: node.TypeLength);
            return new ColumnDefinition
            {
                Name = name,
                Kind = NodeKind.Leaf,
                Repetition = node.Repetition,
                PhysicalType = physicalType,
                LogicalType = logicalType,
                FieldId = node.FieldId,
                Options = options,
                Children = []
            };
        }

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
        var groupChildren = children.MoveToImmutable();
        var logicalType = ConvertLogicalType(metadata, node);
        return new ColumnDefinition
        {
            Name = name,
            Kind = NodeKind.Group,
            Repetition = node.Repetition,
            FieldId = node.FieldId,
            LogicalType = logicalType,
            Children = groupChildren
        };
    }

    static ColumnDefinition BuildListDefinition(PhysicalFileMetadata metadata, ref int index,
        ParquetSchemaNodeInfo node, string name)
    {
        if (node.Repetition == ParquetRepetition.Repeated && !IsNestedRepeatedListElement(metadata, node))
            throw new CorruptParquetException(
                $"LIST schema node '{name}' can only be repeated when nested as an element of another LIST.");
        if (node.Repetition is not
            (ParquetRepetition.Required or ParquetRepetition.Optional or ParquetRepetition.Repeated))
            throw new CorruptParquetException($"LIST schema node '{name}' is missing its repetition.");
        if (node.ChildCount != 1)
            throw new CorruptParquetException($"LIST schema node '{name}' must contain exactly one repeated list child.");
        if ((uint)index >= (uint)metadata.SchemaNodeCount)
            throw new CorruptParquetException($"LIST schema node '{name}' is missing its repeated list child.");

        var repeated = metadata.SchemaNodes[index++];
        if (repeated.Repetition != ParquetRepetition.Repeated)
            throw new CorruptParquetException($"LIST schema node '{name}' list child must be repeated.");

        ColumnDefinition element;
        if (repeated.PhysicalType is { } physicalType)
        {
            if (repeated.ChildCount != 0)
                throw new CorruptParquetException($"LIST schema node '{name}' primitive list child cannot contain elements.");
            element = new ColumnDefinition
            {
                Name = "element",
                Kind = NodeKind.Leaf,
                Repetition = ParquetRepetition.Required,
                PhysicalType = physicalType,
                LogicalType = ConvertLogicalType(metadata, repeated),
                FieldId = repeated.FieldId,
                Options = new ColumnOptions(ParquetRepetition.Required, typeLength: repeated.TypeLength),
                Children = []
            };
        }
        else
        {
            if (repeated.ChildCount == 0)
                throw new CorruptParquetException($"LIST schema node '{name}' repeated child must contain elements.");
            if ((uint)index >= (uint)metadata.SchemaNodeCount)
                throw new CorruptParquetException($"LIST schema node '{name}' repeated child is missing its element.");

            var repeatedName = Encoding.UTF8.GetString(metadata.SchemaNodeNameUtf8(repeated.Ordinal));
            var repeatedIsElement = repeated.ChildCount > 1 ||
                metadata.SchemaNodes[index].Repetition == ParquetRepetition.Repeated ||
                string.Equals(repeatedName, "array", StringComparison.Ordinal) ||
                string.Equals(repeatedName, $"{name}_tuple", StringComparison.Ordinal);
            element = repeatedIsElement
                ? BuildRepeatedGroupElement(metadata, ref index, repeated)
                : BuildDefinition(metadata, ref index) with { Name = "element" };
        }
        return new ColumnDefinition
        {
            Name = name,
            Kind = NodeKind.List,
            Repetition = node.Repetition,
            FieldId = node.FieldId,
            Children = [element]
        };
    }

    static bool IsNestedRepeatedListElement(PhysicalFileMetadata metadata, ParquetSchemaNodeInfo node)
    {
        if ((uint)node.ParentOrdinal >= (uint)metadata.SchemaNodeCount)
            return false;

        var parent = metadata.SchemaNodes[node.ParentOrdinal];
        return parent.Kind == NodeKind.List &&
            parent.PhysicalType is null &&
            parent.ChildCount == 1;
    }

    static ColumnDefinition BuildRepeatedGroupElement(PhysicalFileMetadata metadata, ref int index,
        ParquetSchemaNodeInfo repeated)
    {
        var element = repeated.Kind switch
        {
            NodeKind.List => BuildListDefinition(metadata, ref index, repeated, "element"),
            NodeKind.Map => BuildMapDefinition(metadata, ref index, repeated, "element"),
            _ => BuildGroupDefinition(metadata, ref index, repeated, "element")
        };
        return element with { Repetition = ParquetRepetition.Required };
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
            FieldId = node.FieldId,
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

    static LogicalType? ConvertLogicalType(PhysicalFileMetadata metadata, ParquetSchemaNodeInfo node)
        => node.LogicalType.Kind switch
        {
            LogicalTypeKind.None => null,
            LogicalTypeKind.String => new LogicalType.String(),
            LogicalTypeKind.Json => new LogicalType.Json(),
            LogicalTypeKind.Bson => new LogicalType.Bson(),
            LogicalTypeKind.Enum => new LogicalType.Enum(),
            LogicalTypeKind.Uuid => new LogicalType.Uuid(),
            LogicalTypeKind.Float16 => new LogicalType.Float16(),
            LogicalTypeKind.Interval => new LogicalType.Interval(),
            LogicalTypeKind.Unknown => new LogicalType.Unknown(),
            LogicalTypeKind.Date => new LogicalType.Date(),
            LogicalTypeKind.Time => new LogicalType.Time(node.LogicalType.Unit, node.LogicalType.IsAdjustedToUtc),
            LogicalTypeKind.Timestamp => new LogicalType.Timestamp(node.LogicalType.Unit,
                node.LogicalType.IsAdjustedToUtc),
            LogicalTypeKind.Integer => new LogicalType.Int(node.LogicalType.BitWidth, node.LogicalType.IsSigned),
            LogicalTypeKind.Decimal => BuildDecimal(node.LogicalType.Precision, node.LogicalType.Scale),
            LogicalTypeKind.Variant => new LogicalType.Variant(node.LogicalType.SpecificationVersion),
            LogicalTypeKind.Geometry => new LogicalType.Geometry(ReadCrs(metadata, node)),
            LogicalTypeKind.Geography => new LogicalType.Geography(ReadCrs(metadata, node),
                node.LogicalType.Algorithm),
            _ => throw new NotSupportedException($"Logical type '{node.LogicalType.Kind}' is not supported.")
        };

    // The precision and scale come off the wire, so a bad pair is a malformed file rather than a caller mistake.
    // Checking first keeps LogicalType.Decimal's constructor from raising ArgumentOutOfRangeException at a reader.
    static LogicalType.Decimal BuildDecimal(int precision, int scale)
        => LogicalType.Decimal.DescribeError(precision, scale) is { } error
            ? throw new CorruptParquetException($"Parquet schema is not valid: {error}")
            : new LogicalType.Decimal(precision, scale);

    static string? ReadCrs(PhysicalFileMetadata metadata, ParquetSchemaNodeInfo node)
        => node.LogicalType.HasCrs
            ? Encoding.UTF8.GetString(metadata.SchemaNodeLogicalTypeCrsUtf8(node.Ordinal))
            : null;

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
