using Plank.Writing;

namespace Plank.Reading.Physical;

public sealed class ParquetFileMetadata
{
    internal byte[]? FooterBuffer;
    internal ParquetSchemaNodeInfo[]? SchemaNodeBuffer;
    internal ParquetColumnSchemaInfo[]? ColumnBuffer;
    internal ParquetRowGroupInfo[]? RowGroupBuffer;
    internal ParquetColumnChunkInfo[]? ColumnChunkBuffer;
    internal int FooterByteCount;
    internal int ColumnChunkCount;

    public int FileVersion { get; internal set; }
    public ulong FooterOffset { get; internal set; }
    public uint FooterLength { get; internal set; }
    public int SchemaNodeCount { get; internal set; }
    public int ColumnCount { get; internal set; }
    public int RowGroupCount { get; internal set; }

    public ReadOnlySpan<ParquetSchemaNodeInfo> SchemaNodes
        => SchemaNodeBuffer.AsSpan(0, SchemaNodeCount);

    public ReadOnlySpan<ParquetColumnSchemaInfo> Columns
        => ColumnBuffer.AsSpan(0, ColumnCount);

    public ReadOnlySpan<ParquetRowGroupInfo> RowGroups
        => RowGroupBuffer.AsSpan(0, RowGroupCount);

    public ReadOnlySpan<ParquetColumnChunkInfo> ColumnChunks
        => ColumnChunkBuffer.AsSpan(0, ColumnChunkCount);

    public ReadOnlySpan<byte> SchemaNodeNameUtf8(int nodeOrdinal)
    {
        ValidateOrdinal(nodeOrdinal, SchemaNodeCount, nameof(nodeOrdinal));
        return GetName(SchemaNodeBuffer![nodeOrdinal]);
    }

    public ParquetColumnSchemaInfo ColumnSchema(int columnOrdinal)
    {
        ValidateOrdinal(columnOrdinal, ColumnCount, nameof(columnOrdinal));
        return ColumnBuffer![columnOrdinal];
    }

    public ReadOnlySpan<byte> ColumnPathSegmentUtf8(int columnOrdinal, int segmentOrdinal)
    {
        var column = ColumnSchema(columnOrdinal);
        ValidateOrdinal(segmentOrdinal, column.PathSegmentCount, nameof(segmentOrdinal));
        return GetName(GetPathNodeOrdinal(column, segmentOrdinal));
    }

    public ParquetRowGroupInfo RowGroup(int rowGroupOrdinal)
    {
        ValidateOrdinal(rowGroupOrdinal, RowGroupCount, nameof(rowGroupOrdinal));
        return RowGroupBuffer![rowGroupOrdinal];
    }

    public ParquetColumnChunkInfo ColumnChunk(int rowGroupOrdinal, int columnOrdinal)
    {
        var rowGroup = RowGroup(rowGroupOrdinal);
        ValidateOrdinal(columnOrdinal, rowGroup.ColumnCount, nameof(columnOrdinal));
        return ColumnChunkBuffer![rowGroup.ColumnStart + columnOrdinal];
    }

    internal ReadOnlySpan<byte> FooterBytes
        => FooterBuffer.AsSpan(0, FooterByteCount);

    internal void ReturnBuffers(IParquetBufferPool bufferPool)
    {
        Return(bufferPool, ref FooterBuffer);
        Return(bufferPool, ref SchemaNodeBuffer);
        Return(bufferPool, ref ColumnBuffer);
        Return(bufferPool, ref RowGroupBuffer);
        Return(bufferPool, ref ColumnChunkBuffer);
        Clear();
    }

    ReadOnlySpan<byte> GetName(int nodeOrdinal)
        => GetName(SchemaNodeBuffer![nodeOrdinal]);

    ReadOnlySpan<byte> GetName(ParquetSchemaNodeInfo node)
        => FooterBytes.Slice(node.NameOffset, node.NameLength);

    int GetPathNodeOrdinal(ParquetColumnSchemaInfo column, int segmentOrdinal)
    {
        var nodeOrdinal = column.NodeOrdinal;
        for (var i = column.PathSegmentCount - 1; i > segmentOrdinal; i--)
            nodeOrdinal = SchemaNodeBuffer![nodeOrdinal].ParentOrdinal;
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
        FooterByteCount = 0;
        ColumnChunkCount = 0;
    }

    static void Return<T>(IParquetBufferPool bufferPool, ref T[]? buffer)
    {
        if (buffer is { Length: > 0 })
            bufferPool.Return(buffer);
        buffer = null;
    }

    static void ValidateOrdinal(int ordinal, int count, string parameterName)
    {
        if ((uint)ordinal >= (uint)count)
            throw new ArgumentOutOfRangeException(parameterName, ordinal,
                $"Ordinal must be between zero and {count - 1}.");
    }
}
