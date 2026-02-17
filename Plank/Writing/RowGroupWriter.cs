using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Writing;

public sealed class RowGroupWriter
{
    readonly ParquetWriter _writer;
    BufferWriter _compressedContent;
    int _nextColumnOrdinal;
    int _rowCount;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _compressedContent = default;
        _nextColumnOrdinal = 0;
        _rowCount = -1;
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

        if (_nextColumnOrdinal == 0)
            _rowCount = serialized.RowCount;
        else if (serialized.RowCount != _rowCount)
            throw new InvalidOperationException(
                $"Row count mismatch for row group. Expected {_rowCount}, got {serialized.RowCount}.");

        var column = _writer.ColumnsByOrdinal[serialized.ColumnOrdinal];
        var pages = serialized.Pages;
        var compression = _writer.Compression;
        if (compression != CompressionKind.None && !_compressedContent.IsInitialized)
            _compressedContent = _writer.BufferWriters.CreatePageBufferWriter();
        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        var dataPageOffset = -1L;
        var dictionaryPageOffset = 0L;
        var hasDictionaryPage = false;
        var dataEncoding = EncodingKindResolver.GetDataEncodingKind(column);
        for (var i = 0; i < pages.Count; i++)
        {
            ref var page = ref pages[i];
            var pageOffset = _writer.FileOffset;
            var pageKind = ReadPageKind(ref page.Header);
            var uncompressedContentSize = page.Content.WrittenLength;
            var compressedContentSize = uncompressedContentSize;
            var writeCompressedContent = false;
            if (compression != CompressionKind.None && uncompressedContentSize > 0)
            {
                Compression.Compress(compression, _writer.CompressionContext, ref page.Content, ref _compressedContent);
                compressedContentSize = _compressedContent.WrittenLength;
                writeCompressedContent = true;
            }

            switch (pageKind)
            {
                case PageKind.Dictionary:
                {
                    if (!hasDictionaryPage)
                    {
                        hasDictionaryPage = true;
                        dictionaryPageOffset = pageOffset;
                    }

                    var dictionaryValueCount = ReadDictionaryValueCount(ref page.Header);
                    page.Header.Reset();
                    ParquetMetadataThriftWriter.WriteDictionaryPageHeader(ref page.Header, dictionaryValueCount,
                        uncompressedContentSize, compressedContentSize);
                    break;
                }
                case PageKind.DataV1:
                case PageKind.DataV2:
                {
                    ReadDataPageMetadata(ref page.Header, out var dataPageRowCount, out var pageEncoding);
                    if (dataPageOffset < 0)
                    {
                        dataPageOffset = pageOffset;
                        dataEncoding = pageEncoding;
                    }

                    page.Header.Reset();
                    ParquetMetadataThriftWriter.WriteDataPageHeaderV2(ref page.Header, dataPageRowCount, pageEncoding,
                        uncompressedContentSize, compressedContentSize, writeCompressedContent);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown page kind '{pageKind}'.");
            }

            var headerSize = page.Header.WrittenLength;
            _writer.WriteBuffer(ref page.Header);
            if (!writeCompressedContent)
                _writer.WriteBuffer(ref page.Content);
            else
                _writer.WriteBuffer(ref _compressedContent);

            totalUncompressedSize += checked((long)headerSize + uncompressedContentSize);
            totalCompressedSize += checked((long)headerSize + compressedContentSize);
        }

        if (dataPageOffset < 0)
            dataPageOffset = hasDictionaryPage ? dictionaryPageOffset : _writer.FileOffset;

        ref var columnMetadata = ref _writer.OpenRowGroupColumnMetadata[serialized.ColumnOrdinal];
        columnMetadata.DataPageOffset = dataPageOffset;
        columnMetadata.DictionaryPageOffset = dictionaryPageOffset;
        columnMetadata.ValueCount = serialized.RowCount;
        columnMetadata.TotalUncompressedSize = totalUncompressedSize;
        columnMetadata.TotalCompressedSize = totalCompressedSize;
        columnMetadata.DataEncoding = dataEncoding;
        columnMetadata.Compression = compression;
        columnMetadata.HasDictionaryPage = hasDictionaryPage;

        serialized.Consume();
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal != _writer.ColumnCount)
            return;

        ParquetMetadataThriftWriter.WriteRowGroup(ref _writer.SerializedRowGroupsMetadata, _writer.ColumnsByOrdinal,
            _writer.OpenRowGroupColumnMetadata, _rowCount);
        _writer.CompleteOpenRowGroup(_rowCount);
    }

    static PageKind ReadPageKind(ref BufferWriter header)
    {
        header.TryGetSingleWrittenSpan(out var span);
        return (PageKind)span[0];
    }

    static void ReadDataPageMetadata(ref BufferWriter header, out int rowCount, out EncodingKind encoding)
    {
        header.TryGetSingleWrittenSpan(out var span);
        rowCount = BinaryPrimitives.ReadInt32LittleEndian(span[2..]);
        encoding = (EncodingKind)span[1];
    }

    static int ReadDictionaryValueCount(ref BufferWriter header)
    {
        header.TryGetSingleWrittenSpan(out var span);
        return BinaryPrimitives.ReadInt32LittleEndian(span[1..]);
    }
}
