using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading.Physical.Internal;

sealed class PhysicalMetadataStore
{
    readonly IParquetBufferPool _bufferPool;

    internal PhysicalMetadataStore(IParquetBufferPool bufferPool)
        => _bufferPool = bufferPool;

    internal byte[] FooterBytes = [];
    internal PhysicalSchemaNode[] SchemaNodes = [];
    internal PhysicalColumnSchema[] Columns = [];
    internal PhysicalRowGroup[] RowGroups = [];
    internal ParquetColumnChunkInfo[] ColumnChunks = [];
    internal int[] ParentStack = [];
    internal int[] RemainingChildren = [];

    internal int FooterByteCount;
    internal int SchemaNodeCount;
    internal int ColumnCount;
    internal int RowGroupCount;
    internal int ColumnChunkCount;
    internal int FileVersion;
    internal ulong FooterOffset;
    internal uint FooterLength;

    internal void Clear()
    {
        FooterByteCount = 0;
        SchemaNodeCount = 0;
        ColumnCount = 0;
        RowGroupCount = 0;
        ColumnChunkCount = 0;
        FileVersion = 0;
        FooterOffset = 0;
        FooterLength = 0;
    }

    internal Span<byte> PrepareFooter(uint length)
    {
        Ensure(ref FooterBytes, checked((int)length));
        FooterByteCount = checked((int)length);
        return FooterBytes.AsSpan(0, FooterByteCount);
    }

    internal void EnsureSchemaNodes(int count)
    {
        Ensure(ref SchemaNodes, count);
        Ensure(ref ParentStack, count);
        Ensure(ref RemainingChildren, count);
    }

    internal void EnsureColumns(int count)
        => Ensure(ref Columns, count);

    internal void EnsureRowGroups(int count)
        => Ensure(ref RowGroups, count);

    internal void EnsureColumnChunks(int count)
        => Ensure(ref ColumnChunks, count);

    internal void Dispose()
    {
        Return(ref FooterBytes);
        Return(ref SchemaNodes);
        Return(ref Columns);
        Return(ref RowGroups);
        Return(ref ColumnChunks);
        Return(ref ParentStack);
        Return(ref RemainingChildren);
        Clear();
    }

    void Ensure<T>(ref T[] buffer, int count)
    {
        if (buffer.Length >= count)
            return;

        var replacement = _bufferPool.Rent<T>(checked((uint)count));
        if (buffer.Length != 0)
        {
            buffer.AsSpan().CopyTo(replacement);
            _bufferPool.Return(buffer);
        }
        buffer = replacement;
    }

    void Return<T>(ref T[] buffer)
    {
        if (buffer.Length == 0)
            return;

        _bufferPool.Return(buffer);
        buffer = [];
    }
}
