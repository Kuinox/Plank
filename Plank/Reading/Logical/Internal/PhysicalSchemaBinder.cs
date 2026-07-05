using System.Text;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using PhysicalFileMetadata = Plank.Reading.Physical.ParquetFileMetadata;

namespace Plank.Reading.Logical.Internal;

static class PhysicalSchemaBinder
{
    internal static InternalParquetFooter Bind(ParquetFileReader physicalReader, ParquetSchema requestedSchema,
        InternalParquetFooter previous, bool strict, IParquetBufferPool bufferPool)
    {
        var requestedColumns = requestedSchema.Columns;
        int[]? rentedOrdinals = null;
        Span<int> projectedOrdinals = requestedColumns.Length <= 256
            ? stackalloc int[requestedColumns.Length]
            : (rentedOrdinals = bufferPool.Rent<int>(checked((uint)requestedColumns.Length)))
                .AsSpan(0, requestedColumns.Length);
        try
        {
            var metadata = physicalReader.Metadata;
            if (strict)
                BuildStrictProjection(metadata, requestedSchema, projectedOrdinals, bufferPool);
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
                    var path = columnOrdinal < requestedColumns.Length ? requestedColumns[columnOrdinal].Name : string.Empty;
                    var encodings = ReuseEncodings(columns[columnOrdinal].Encodings, physicalChunk);
                    columns[columnOrdinal] = new InternalColumnChunkMetadata(physicalChunk, encodings, path);
                }

                rowGroups[rowGroupOrdinal] = new InternalRowGroupMetadata(rowGroupOrdinal, physicalRowGroup.MetadataOffset,
                    physicalRowGroup.ColumnChunkOffset, physicalRowGroup.RowCount, columns);
            }

            return new InternalParquetFooter(metadata.FileVersion, rowGroups);
        }
        finally
        {
            if (rentedOrdinals is not null)
                bufferPool.Return(rentedOrdinals);
        }
    }

    static void BuildStrictProjection(PhysicalFileMetadata metadata, ParquetSchema requestedSchema,
        Span<int> projectedOrdinals, IParquetBufferPool bufferPool)
    {
        var requestedColumns = requestedSchema.Columns;
        for (var requestedOrdinal = 0; requestedOrdinal < requestedColumns.Length; requestedOrdinal++)
        {
            var requested = requestedColumns[requestedOrdinal];
            var match = -1;
            for (var fileOrdinal = 0; fileOrdinal < metadata.ColumnCount; fileOrdinal++)
            {
                var fileColumn = metadata.ColumnSchema(fileOrdinal);
                if (!PathEquals(metadata, fileColumn, requested.Name, bufferPool))
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

    static bool PathEquals(PhysicalFileMetadata metadata, ParquetColumnSchemaInfo column, string requestedPath,
        IParquetBufferPool bufferPool)
    {
        var byteCount = Encoding.UTF8.GetByteCount(requestedPath);
        byte[]? rented = null;
        Span<byte> requestedBytes = byteCount <= 1024
            ? stackalloc byte[byteCount]
            : (rented = bufferPool.Rent<byte>(checked((uint)byteCount))).AsSpan(0, byteCount);
        try
        {
            Encoding.UTF8.GetBytes(requestedPath, requestedBytes);
            var offset = 0;
            for (var segmentOrdinal = 0; segmentOrdinal < column.PathSegmentCount; segmentOrdinal++)
            {
                var segment = metadata.ColumnPathSegmentUtf8(column.Ordinal, segmentOrdinal);
                if (segmentOrdinal > 0)
                {
                    if ((uint)offset >= (uint)requestedBytes.Length || requestedBytes[offset++] != (byte)'.')
                        return false;
                }
                if (segment.Length > requestedBytes.Length - offset ||
                    !segment.SequenceEqual(requestedBytes.Slice(offset, segment.Length)))
                    return false;
                offset += segment.Length;
            }
            return offset == requestedBytes.Length;
        }
        finally
        {
            if (rented is not null)
                bufferPool.Return(rented);
        }
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
