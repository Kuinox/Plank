using Plank.Writing;

namespace Plank.Reading.Physical;

public sealed class ParquetFileMetadata
{
    internal ParquetBuffer FooterBuffer;
    internal ParquetBuffer SchemaNodeBuffer;
    internal ParquetBuffer ColumnBuffer;
    internal ParquetBuffer RowGroupBuffer;
    internal ParquetBuffer ColumnChunkBuffer;
    internal int FooterByteCount;
    internal int ColumnChunkCount;

    public int FileVersion { get; internal set; }
    public ulong FooterOffset { get; internal set; }
    public uint FooterLength { get; internal set; }
    public int SchemaNodeCount { get; internal set; }
    public int ColumnCount { get; internal set; }
    public int RowGroupCount { get; internal set; }

    public ReadOnlySpan<ParquetSchemaNodeInfo> SchemaNodes
        => ParquetBuffer.AsReadOnlySpan<ParquetSchemaNodeInfo>(SchemaNodeBuffer, SchemaNodeCount);

    public ReadOnlySpan<ParquetColumnSchemaInfo> Columns
        => ParquetBuffer.AsReadOnlySpan<ParquetColumnSchemaInfo>(ColumnBuffer, ColumnCount);

    public ReadOnlySpan<ParquetRowGroupInfo> RowGroups
        => ParquetBuffer.AsReadOnlySpan<ParquetRowGroupInfo>(RowGroupBuffer, RowGroupCount);

    public ReadOnlySpan<ParquetColumnChunkInfo> ColumnChunks
        => ParquetBuffer.AsReadOnlySpan<ParquetColumnChunkInfo>(ColumnChunkBuffer, ColumnChunkCount);

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

    public ReadOnlySpan<byte> SchemaNodeNameUtf8(int nodeOrdinal)
    {
        ValidateOrdinal(nodeOrdinal, SchemaNodeCount, nameof(nodeOrdinal));
        return GetName(SchemaNodes[nodeOrdinal]);
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

    internal ReadOnlySpan<byte> FooterBytes
        => FooterBuffer.Span[..FooterByteCount];

    internal void ReturnBuffers()
    {
        FooterBuffer.Dispose();
        SchemaNodeBuffer.Dispose();
        ColumnBuffer.Dispose();
        RowGroupBuffer.Dispose();
        ColumnChunkBuffer.Dispose();
        Clear();
    }

    ReadOnlySpan<byte> GetName(int nodeOrdinal)
        => GetName(SchemaNodes[nodeOrdinal]);

    ReadOnlySpan<byte> GetName(ParquetSchemaNodeInfo node)
        => FooterBytes.Slice(node.NameOffset, node.NameLength);

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
        FooterByteCount = 0;
        ColumnChunkCount = 0;
    }

    static void ValidateOrdinal(int ordinal, int count, string parameterName)
    {
        if ((uint)ordinal >= (uint)count)
            throw new ArgumentOutOfRangeException(parameterName, ordinal,
                $"Ordinal must be between zero and {count - 1}.");
    }
}
