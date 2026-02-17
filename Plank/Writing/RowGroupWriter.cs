using System.Buffers.Binary;

namespace Plank.Writing;

public sealed class RowGroupWriter
{
    readonly ParquetWriter _writer;
    BufferWriter _compressedContent;
    int _nextColumnOrdinal;
    int _rowCount;
    bool _metadataStarted;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _compressedContent = default;
        _nextColumnOrdinal = 0;
        _rowCount = -1;
        _metadataStarted = false;
    }

    public void Write(SerializedColumn serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        if (serialized.ColumnOrdinal < 0)
            throw new InvalidOperationException(
                "SerializedColumn has no serialized data. Call serialized.Serialize(column, values) before Write(...).");

        if (serialized.ColumnOrdinal != _nextColumnOrdinal)
        {
            var expectedColumn = _writer.ColumnsByOrdinal[_nextColumnOrdinal];
            var actualColumn = _writer.ColumnsByOrdinal[serialized.ColumnOrdinal];
            throw new InvalidOperationException(
                $"Invalid column order for this row group. Expected '{expectedColumn.Name}' (ordinal {_nextColumnOrdinal}) next, but got '{actualColumn.Name}' (ordinal {serialized.ColumnOrdinal}). Write columns in schema order.");
        }

        if (!_metadataStarted)
        {
            _rowCount = serialized.RowCount;
            WriteRowGroupHeaderMetadata(_rowCount);
            _metadataStarted = true;
        }
        else if (serialized.RowCount != _rowCount)
            throw new InvalidOperationException(
                $"Row count mismatch for row group. Expected {_rowCount}, got {serialized.RowCount}.");

        var pages = serialized.Pages;
        var compression = _writer.Compression;
        if (compression != CompressionKind.None && !_compressedContent.IsInitialized)
            _compressedContent = _writer.BufferWriters.CreatePageBufferWriter();
        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            ref var page = ref pages[i];
            var headerSize = page.Header.WrittenLength;
            var uncompressedContentSize = page.Content.WrittenLength;
            var compressedContentSize = uncompressedContentSize;
            _writer.WriteBuffer(ref page.Header);
            if (compression == CompressionKind.None || uncompressedContentSize == 0)
                _writer.WriteBuffer(ref page.Content);
            else
            {
                Compression.Compress(compression, _writer.BufferWriters, ref page.Content, ref _compressedContent);
                compressedContentSize = _compressedContent.WrittenLength;
                _writer.WriteBuffer(ref _compressedContent);
            }

            totalUncompressedSize += checked((long)headerSize + uncompressedContentSize);
            totalCompressedSize += checked((long)headerSize + compressedContentSize);
        }

        WriteColumnMetadata(serialized.RowCount, totalUncompressedSize, totalCompressedSize);

        serialized.Consume();
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal == _writer.ColumnCount)
            _writer.CompleteOpenRowGroup();
    }

    void WriteRowGroupHeaderMetadata(int rowCount)
    {
        const int size = sizeof(int) + sizeof(int);
        ref var metadata = ref _writer.SerializedRowGroupsMetadata;
        var span = metadata.GetSpan(size);
        BinaryPrimitives.WriteInt32LittleEndian(span[0..], rowCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], _writer.ColumnCount);
        metadata.Advance(size);
    }

    void WriteColumnMetadata(int rowCount, long totalUncompressedSize, long totalCompressedSize)
    {
        const int size = sizeof(int) + sizeof(int) + sizeof(long) + sizeof(long);
        ref var metadata = ref _writer.SerializedRowGroupsMetadata;
        var span = metadata.GetSpan(size);
        BinaryPrimitives.WriteInt32LittleEndian(span[0..], rowCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], rowCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], totalUncompressedSize);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], totalCompressedSize);
        metadata.Advance(size);
    }
}
