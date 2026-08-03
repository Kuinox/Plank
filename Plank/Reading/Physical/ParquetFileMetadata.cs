using Plank.Schema;

namespace Plank.Reading.Physical;

public sealed class ParquetFileMetadata
{
    internal ParquetBuffer FooterBuffer;
    internal ParquetBuffer SchemaNodeBuffer;
    internal ParquetBuffer ColumnBuffer;
    internal ParquetBuffer RowGroupBuffer;
    internal ParquetBuffer ColumnChunkBuffer;
    internal ParquetBuffer SortingColumnBuffer;
    internal int FooterByteCount;
    internal int ColumnChunkCount;
    internal int SortingColumnCount;
    internal ParquetBuffer KeyValueMetadataBuffer;
    internal int CreatedByOffset;
    internal int CreatedByLength;

    public int FileVersion { get; internal set; }
    public ulong FooterOffset { get; internal set; }
    public uint FooterLength { get; internal set; }
    public int SchemaNodeCount { get; internal set; }
    public int ColumnCount { get; internal set; }
    public int RowGroupCount { get; internal set; }
    public int KeyValueMetadataCount { get; internal set; }
    public bool HasCreatedBy { get; internal set; }

    public ReadOnlySpan<ParquetSchemaNodeInfo> SchemaNodes
        => ParquetBuffer.AsReadOnlySpan<ParquetSchemaNodeInfo>(SchemaNodeBuffer, SchemaNodeCount);

    public ReadOnlySpan<ParquetColumnSchemaInfo> Columns
        => ParquetBuffer.AsReadOnlySpan<ParquetColumnSchemaInfo>(ColumnBuffer, ColumnCount);

    public ReadOnlySpan<ParquetRowGroupInfo> RowGroups
        => ParquetBuffer.AsReadOnlySpan<ParquetRowGroupInfo>(RowGroupBuffer, RowGroupCount);

    public ReadOnlySpan<ParquetColumnChunkInfo> ColumnChunks
        => ParquetBuffer.AsReadOnlySpan<ParquetColumnChunkInfo>(ColumnChunkBuffer, ColumnChunkCount);

    public ReadOnlySpan<ParquetSortingColumn> SortingColumns
        => ParquetBuffer.AsReadOnlySpan<ParquetSortingColumn>(SortingColumnBuffer, SortingColumnCount);

    public ReadOnlySpan<ParquetKeyValueMetadataInfo> KeyValueMetadata
        => ParquetBuffer.AsReadOnlySpan<ParquetKeyValueMetadataInfo>(KeyValueMetadataBuffer,
            KeyValueMetadataCount);

    public ReadOnlySpan<byte> CreatedByUtf8
        => HasCreatedBy ? FooterBytes.Slice(CreatedByOffset, CreatedByLength) : [];

    internal Span<ParquetSchemaNodeInfo> SchemaNodeStorage
        => ParquetBuffer.AsSpan<ParquetSchemaNodeInfo>(SchemaNodeBuffer,
            SchemaNodeBuffer.Length / System.Runtime.CompilerServices.Unsafe.SizeOf<ParquetSchemaNodeInfo>());

    internal Span<ParquetColumnSchemaInfo> ColumnStorage
        => ParquetBuffer.AsSpan<ParquetColumnSchemaInfo>(ColumnBuffer,
            ColumnBuffer.Length / System.Runtime.CompilerServices.Unsafe.SizeOf<ParquetColumnSchemaInfo>());

    internal Span<ParquetRowGroupInfo> RowGroupStorage
        => ParquetBuffer.AsSpan<ParquetRowGroupInfo>(RowGroupBuffer,
            RowGroupBuffer.Length / System.Runtime.CompilerServices.Unsafe.SizeOf<ParquetRowGroupInfo>());

    internal Span<ParquetColumnChunkInfo> ColumnChunkStorage
        => ParquetBuffer.AsSpan<ParquetColumnChunkInfo>(ColumnChunkBuffer,
            ColumnChunkBuffer.Length / System.Runtime.CompilerServices.Unsafe.SizeOf<ParquetColumnChunkInfo>());

    internal Span<ParquetSortingColumn> SortingColumnStorage
        => ParquetBuffer.AsSpan<ParquetSortingColumn>(SortingColumnBuffer,
            SortingColumnBuffer.Length / System.Runtime.CompilerServices.Unsafe.SizeOf<ParquetSortingColumn>());

    internal Span<ParquetKeyValueMetadataInfo> KeyValueMetadataStorage
        => ParquetBuffer.AsSpan<ParquetKeyValueMetadataInfo>(KeyValueMetadataBuffer,
            KeyValueMetadataBuffer.Length /
            System.Runtime.CompilerServices.Unsafe.SizeOf<ParquetKeyValueMetadataInfo>());

    public ReadOnlySpan<byte> SchemaNodeNameUtf8(int nodeOrdinal)
    {
        ValidateOrdinal(nodeOrdinal, SchemaNodeCount, nameof(nodeOrdinal));
        return GetName(SchemaNodes[nodeOrdinal]);
    }

    public ReadOnlySpan<byte> SchemaNodeLogicalTypeCrsUtf8(int nodeOrdinal)
    {
        ValidateOrdinal(nodeOrdinal, SchemaNodeCount, nameof(nodeOrdinal));
        return GetLogicalTypeCrsUtf8(SchemaNodes[nodeOrdinal].LogicalType);
    }

    public ParquetColumnSchemaInfo ColumnSchema(int columnOrdinal)
    {
        ValidateOrdinal(columnOrdinal, ColumnCount, nameof(columnOrdinal));
        return Columns[columnOrdinal];
    }

    public ReadOnlySpan<byte> ColumnPathSegmentUtf8(int columnOrdinal, int segmentOrdinal)
    {
        var column = ColumnSchema(columnOrdinal);
        ValidateOrdinal(segmentOrdinal, column.PathSegmentCount, nameof(segmentOrdinal));
        return GetName(GetPathNodeOrdinal(column, segmentOrdinal));
    }

    public ReadOnlySpan<byte> ColumnLogicalTypeCrsUtf8(int columnOrdinal)
    {
        var column = ColumnSchema(columnOrdinal);
        return GetLogicalTypeCrsUtf8(column.LogicalType);
    }

    public ParquetRowGroupInfo RowGroup(int rowGroupOrdinal)
    {
        ValidateOrdinal(rowGroupOrdinal, RowGroupCount, nameof(rowGroupOrdinal));
        return RowGroups[rowGroupOrdinal];
    }

    public ParquetColumnChunkInfo ColumnChunk(int rowGroupOrdinal, int columnOrdinal)
    {
        var rowGroup = RowGroup(rowGroupOrdinal);
        ValidateOrdinal(columnOrdinal, rowGroup.ColumnCount, nameof(columnOrdinal));
        return ColumnChunks[rowGroup.ColumnStart + columnOrdinal];
    }

    public ReadOnlySpan<ParquetSortingColumn> RowGroupSortingColumns(int rowGroupOrdinal)
    {
        var rowGroup = RowGroup(rowGroupOrdinal);
        return SortingColumns.Slice(rowGroup.SortingColumnStart, rowGroup.SortingColumnCount);
    }

    public ReadOnlySpan<byte> KeyValueMetadataKeyUtf8(int metadataOrdinal)
    {
        ValidateOrdinal(metadataOrdinal, KeyValueMetadataCount, nameof(metadataOrdinal));
        var entry = KeyValueMetadata[metadataOrdinal];
        return FooterBytes.Slice(entry.KeyOffset, entry.KeyLength);
    }

    public ReadOnlySpan<byte> KeyValueMetadataValueUtf8(int metadataOrdinal)
    {
        ValidateOrdinal(metadataOrdinal, KeyValueMetadataCount, nameof(metadataOrdinal));
        var entry = KeyValueMetadata[metadataOrdinal];
        return entry.HasValue ? FooterBytes.Slice(entry.ValueOffset, entry.ValueLength) : [];
    }

    internal ReadOnlySpan<byte> FooterBytes
        => FooterBuffer.Span[..FooterByteCount];

    internal void ReturnBuffers()
    {
        FooterBuffer.Dispose();
        SchemaNodeBuffer.Dispose();
        ColumnBuffer.Dispose();
        RowGroupBuffer.Dispose();
        ColumnChunkBuffer.Dispose();
        SortingColumnBuffer.Dispose();
        KeyValueMetadataBuffer.Dispose();
        Clear();
    }

    ReadOnlySpan<byte> GetName(int nodeOrdinal)
        => GetName(SchemaNodes[nodeOrdinal]);

    ReadOnlySpan<byte> GetName(ParquetSchemaNodeInfo node)
        => FooterBytes.Slice(node.NameOffset, node.NameLength);

    ReadOnlySpan<byte> GetLogicalTypeCrsUtf8(LogicalTypeInfo logicalType)
        => logicalType.HasCrs
            ? FooterBytes.Slice(logicalType.CrsOffset, logicalType.CrsLength)
            : [];

    int GetPathNodeOrdinal(ParquetColumnSchemaInfo column, int segmentOrdinal)
    {
        var nodeOrdinal = column.NodeOrdinal;
        for (var i = column.PathSegmentCount - 1; i > segmentOrdinal; i--)
            nodeOrdinal = SchemaNodes[nodeOrdinal].ParentOrdinal;
        return nodeOrdinal;
    }

    void Clear()
    {
        FileVersion = 0;
        FooterOffset = 0;
        FooterLength = 0;
        SchemaNodeCount = 0;
        ColumnCount = 0;
        RowGroupCount = 0;
        KeyValueMetadataCount = 0;
        HasCreatedBy = false;
        CreatedByOffset = 0;
        CreatedByLength = 0;
        FooterByteCount = 0;
        ColumnChunkCount = 0;
        SortingColumnCount = 0;
    }

    static void ValidateOrdinal(int ordinal, int count, string parameterName)
    {
        if ((uint)ordinal >= (uint)count)
            throw new ArgumentOutOfRangeException(parameterName, ordinal,
                $"Ordinal must be between zero and {count - 1}.");
    }
}
