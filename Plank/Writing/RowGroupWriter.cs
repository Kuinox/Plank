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
            if (TryReadPageKind(ref page.Header, out var pageKind))
            {
                if (!hasDictionaryPage && pageKind == PageKind.Dictionary)
                {
                    hasDictionaryPage = true;
                    dictionaryPageOffset = pageOffset;
                }

                if (dataPageOffset < 0 && (pageKind == PageKind.DataV1 || pageKind == PageKind.DataV2))
                {
                    dataPageOffset = pageOffset;
                    if (TryReadDataPageEncoding(ref page.Header, out var headerEncoding))
                        dataEncoding = headerEncoding;
                }
            }

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

    static bool TryReadPageKind(ref BufferWriter header, out PageKind pageKind)
    {
        if (!header.TryGetSingleWrittenSpan(out var span) || span.Length == 0)
        {
            pageKind = default;
            return false;
        }

        pageKind = (PageKind)span[0];
        return pageKind == PageKind.DataV1 || pageKind == PageKind.DataV2 || pageKind == PageKind.Dictionary;
    }

    static bool TryReadDataPageEncoding(ref BufferWriter header, out EncodingKind encoding)
    {
        if (!header.TryGetSingleWrittenSpan(out var span) || span.Length < 2)
        {
            encoding = default;
            return false;
        }

        var value = span[1];
        if (value > (byte)EncodingKind.ByteStreamSplit)
        {
            encoding = default;
            return false;
        }

        encoding = (EncodingKind)value;
        return true;
    }
}
