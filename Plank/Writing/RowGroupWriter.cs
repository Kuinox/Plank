using Plank.Schema;
using Plank.Writing.Compression;
using Plank.Writing.Encoding;
using Plank.Writing.Thrift;

namespace Plank.Writing;

public sealed class RowGroupWriter
{
    readonly ParquetWriter _writer;
    BufferWriter _compressedContent;
    BufferWriter _compressionInput;
    BufferWriter _compressedValues;
    uint _nextColumnOrdinal;
    int _rowCount;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _compressedContent = default;
        _compressionInput = default;
        _compressedValues = default;
        ResetForNewRowGroup();
    }

    internal void ResetForNewRowGroup()
    {
        _nextColumnOrdinal = 0;
        _rowCount = -1;
    }

    public SerializedColumn CreateSerializedColumn()
        => _writer.CreateSerializedColumn();

    public void Write(SerializedColumn serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        if (!serialized.HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn has no serialized data. Call serialized.Serialize(column, values) before Write(...).");

        if (serialized.ColumnOrdinal != _nextColumnOrdinal)
        {
            var expectedColumn = _writer.ColumnsByOrdinal[(int)_nextColumnOrdinal];
            var actualColumn = _writer.ColumnsByOrdinal[(int)serialized.ColumnOrdinal];
            throw new InvalidOperationException(
                $"Invalid column order for this row group. Expected '{expectedColumn.Name}' (ordinal {_nextColumnOrdinal}) next, but got '{actualColumn.Name}' (ordinal {serialized.ColumnOrdinal}). Write columns in schema order.");
        }

        if (_nextColumnOrdinal == 0)
            _rowCount = serialized.RowCount;
        else if (serialized.RowCount != _rowCount)
            throw new InvalidOperationException(
                $"Row count mismatch for row group. Expected {_rowCount}, got {serialized.RowCount}.");

        var columnOrdinal = (int)serialized.ColumnOrdinal;
        var column = _writer.ColumnsByOrdinal[columnOrdinal];
        var pages = serialized.Pages;
        var compression = _writer.Compression;
        if (compression != CompressionKind.None && !_compressedContent.IsInitialized)
            _compressedContent = _writer.BufferWriters.CreatePageBufferWriter();
        if (compression != CompressionKind.None && !_compressionInput.IsInitialized)
            _compressionInput = _writer.BufferWriters.CreatePageBufferWriter();
        if (compression != CompressionKind.None && !_compressedValues.IsInitialized)
            _compressedValues = _writer.BufferWriters.CreatePageBufferWriter();
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
            var pageKind = page.Kind;
            var pageContentSize = page.Content.WrittenLength;
            var compressedContentSize = pageContentSize;
            var uncompressedPageHeaderSize = pageContentSize;
            var writeCompressedContent = false;
            var storedContentSize = pageContentSize;

            switch (pageKind)
            {
                case PageKind.Dictionary:
                {
                    if (compression != CompressionKind.None && pageContentSize > 0)
                    {
                        Plank.Writing.Compression.Compression.Compress(compression, _writer.CompressionContext,
                            ref page.Content, ref _compressedContent);
                        compressedContentSize = _compressedContent.WrittenLength;
                        storedContentSize = compressedContentSize;
                        writeCompressedContent = true;
                    }

                    if (!hasDictionaryPage)
                    {
                        hasDictionaryPage = true;
                        dictionaryPageOffset = pageOffset;
                    }

                    var dictionaryValueCount = page.DictionaryValueCount;
                    page.Header.Reset();
                    ParquetMetadataThriftWriter.WriteDictionaryPageHeader(ref page.Header, dictionaryValueCount,
                        pageContentSize, compressedContentSize);
                    break;
                }
                case PageKind.DataV1:
                case PageKind.DataV2:
                {
                    var dataPageRowCount = page.RowCount;
                    var dataPageValueCount = page.ValueCount;
                    var dataPageNullCount = page.NullCount;
                    var repetitionLevelsByteLength = page.RepetitionLevelsByteLength;
                    var definitionLevelsByteLength = page.DefinitionLevelsByteLength;
                    var pageEncoding = page.Encoding;
                    var levelBytes = checked(repetitionLevelsByteLength + definitionLevelsByteLength);
                    if ((uint)levelBytes > (uint)pageContentSize)
                        throw new InvalidOperationException(
                            $"Invalid level byte lengths ({levelBytes}) for data page content size {pageContentSize}.");
                    var valueBytes = pageContentSize - levelBytes;
                    uncompressedPageHeaderSize = pageContentSize;
                    compressedContentSize = pageContentSize;
                    storedContentSize = pageContentSize;

                    if (compression != CompressionKind.None && valueBytes > 0)
                    {
                        if (levelBytes == 0)
                        {
                            Plank.Writing.Compression.Compression.Compress(compression, _writer.CompressionContext,
                                ref page.Content, ref _compressedContent);
                            compressedContentSize = _compressedContent.WrittenLength;
                        }
                        else
                        {
                            _compressionInput.Reset();
                            _compressedValues.Reset();
                            _compressedContent.Reset();

                            var source = _writer.CompressionContext.GetContiguousSourceSpan(ref page.Content);
                            var levels = source[..levelBytes];
                            var values = source[levelBytes..];
                            _compressionInput.Write(values);
                            Plank.Writing.Compression.Compression.Compress(compression, _writer.CompressionContext,
                                ref _compressionInput, ref _compressedValues);
                            _compressedContent.Write(levels);
                            _compressedContent.CopyFrom(ref _compressedValues);
                            compressedContentSize = _compressedContent.WrittenLength;
                        }

                        storedContentSize = compressedContentSize;
                        writeCompressedContent = true;
                    }

                    if (dataPageOffset < 0)
                    {
                        dataPageOffset = pageOffset;
                        dataEncoding = pageEncoding;
                    }

                    page.Header.Reset();
                    ParquetMetadataThriftWriter.WriteDataPageHeaderV2(ref page.Header, dataPageRowCount,
                        dataPageValueCount, dataPageNullCount, repetitionLevelsByteLength, definitionLevelsByteLength,
                        pageEncoding, uncompressedPageHeaderSize, compressedContentSize, writeCompressedContent);
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

            totalUncompressedSize += checked((long)headerSize + pageContentSize);
            totalCompressedSize += checked((long)headerSize + storedContentSize);
        }

        if (dataPageOffset < 0)
            dataPageOffset = hasDictionaryPage ? dictionaryPageOffset : _writer.FileOffset;

        ref var columnMetadata = ref _writer.OpenRowGroupColumnMetadata[columnOrdinal];
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
        if (_nextColumnOrdinal != (uint)_writer.ColumnCount)
            return;

        ParquetMetadataThriftWriter.WriteRowGroup(ref _writer.SerializedRowGroupsMetadata, _writer.ColumnsByOrdinal,
            _writer.ColumnPathsByOrdinal, _writer.OpenRowGroupColumnMetadata, _rowCount);
        _writer.CompleteOpenRowGroup(_rowCount);
    }
}
