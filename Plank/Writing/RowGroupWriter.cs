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
    BufferWriter _columnIndexBuffer;
    BufferWriter _offsetIndexBuffer;
    ColumnStatistics[][] _pageStatisticsByColumn;
    PageLocation[][] _pageLocationsByColumn;
    uint _nextColumnOrdinal;
    int _rowCount;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _compressedContent = default;
        _compressionInput = default;
        _compressedValues = default;
        _pageStatisticsByColumn = writer.ColumnCount == 0 ? [] : new ColumnStatistics[writer.ColumnCount][];
        _pageLocationsByColumn = writer.ColumnCount == 0 ? [] : new PageLocation[writer.ColumnCount][];
        ResetForNewRowGroup();
    }

    internal void ResetForNewRowGroup()
    {
        _nextColumnOrdinal = 0;
        _rowCount = -1;
    }

    public SerializedColumn<T> CreateSerializedColumn<T>(Column column)
        => _writer.CreateSerializedColumn<T>(column);

    public void Write<T>(SerializedColumn<T> serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        ISerializedColumn state = serialized;
        if (!state.HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn has no serialized data. Call serialized.Serialize(values) before Write(...).");

        if (state.ColumnOrdinal != _nextColumnOrdinal)
        {
            var expectedColumn = _writer.ColumnsByOrdinal[(int)_nextColumnOrdinal];
            var actualColumn = _writer.ColumnsByOrdinal[(int)state.ColumnOrdinal];
            throw new InvalidOperationException(
                $"Invalid column order for this row group. Expected '{expectedColumn.Name}' (ordinal {_nextColumnOrdinal}) next, but got '{actualColumn.Name}' (ordinal {state.ColumnOrdinal}). Write columns in schema order.");
        }

        if (_nextColumnOrdinal == 0)
            _rowCount = state.RowCount;
        else if (state.RowCount != _rowCount)
            throw new InvalidOperationException(
                $"Row count mismatch for row group. Expected {_rowCount}, got {state.RowCount}.");

        var columnOrdinal = (int)state.ColumnOrdinal;
        var column = _writer.ColumnsByOrdinal[columnOrdinal];
        var pages = state.Pages;
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
        var nullCount = 0L;
        EnsurePageIndexCapacity(columnOrdinal, pages.Count);
        var pageStatistics = _pageStatisticsByColumn[columnOrdinal];
        var pageLocations = _pageLocationsByColumn[columnOrdinal];
        var dataPageCount = 0;
        var firstRowIndex = 0L;
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
                    nullCount = checked(nullCount + dataPageNullCount);
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
            if (pageKind != PageKind.DataV2)
                continue;
            pageStatistics[dataPageCount] = page.Statistics.HasStatistics
                ? page.Statistics.WithNullCount(page.NullCount)
                : ColumnStatistics.Empty(page.NullCount);
            pageLocations[dataPageCount] = new PageLocation(pageOffset, checked(headerSize + storedContentSize),
                firstRowIndex);
            dataPageCount++;
            firstRowIndex = checked(firstRowIndex + page.RowCount);
        }

        if (dataPageOffset < 0)
            dataPageOffset = hasDictionaryPage ? dictionaryPageOffset : _writer.FileOffset;

        ref var columnMetadata = ref _writer.OpenRowGroupColumnMetadata[columnOrdinal];
        columnMetadata.DataPageOffset = dataPageOffset;
        columnMetadata.DictionaryPageOffset = dictionaryPageOffset;
        columnMetadata.ValueCount = state.RowCount;
        columnMetadata.TotalUncompressedSize = totalUncompressedSize;
        columnMetadata.TotalCompressedSize = totalCompressedSize;
        columnMetadata.DataEncoding = dataEncoding;
        columnMetadata.Compression = compression;
        columnMetadata.Statistics = state.Statistics.HasStatistics
            ? state.Statistics.WithNullCount(nullCount)
            : ColumnStatistics.Empty(nullCount);
        columnMetadata.HasDictionaryPage = hasDictionaryPage;
        columnMetadata.ColumnIndexOffset = 0;
        columnMetadata.ColumnIndexLength = 0;
        columnMetadata.OffsetIndexOffset = 0;
        columnMetadata.OffsetIndexLength = 0;
        columnMetadata.PageIndex = new PageIndex(pageStatistics, pageLocations, dataPageCount);

        state.Consume();
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal != (uint)_writer.ColumnCount)
            return;

        WritePageIndexes(_writer.OpenRowGroupColumnMetadata);
        ParquetMetadataThriftWriter.WriteRowGroup(ref _writer.SerializedRowGroupsMetadata, _writer.ColumnsByOrdinal,
            _writer.ColumnPathsByOrdinal, _writer.OpenRowGroupColumnMetadata, _rowCount);
        _writer.CompleteOpenRowGroup(_rowCount);
    }

    void EnsurePageIndexCapacity(int columnOrdinal, int pageCount)
    {
        var statistics = _pageStatisticsByColumn[columnOrdinal];
        if (statistics is null || statistics.Length < pageCount)
        {
            Array.Resize(ref statistics, pageCount);
            _pageStatisticsByColumn[columnOrdinal] = statistics;
        }

        var locations = _pageLocationsByColumn[columnOrdinal];
        if (locations is not null && locations.Length >= pageCount)
            return;

        Array.Resize(ref locations, pageCount);
        _pageLocationsByColumn[columnOrdinal] = locations;
    }

    void WritePageIndexes(Span<ColumnChunkMetadata> metadata)
    {
        for (var i = 0; i < metadata.Length; i++)
            WritePageIndexes(ref metadata[i]);
    }

    void WritePageIndexes(ref ColumnChunkMetadata metadata)
    {
        if (!metadata.PageIndex.HasPages)
            return;

        if (!_columnIndexBuffer.IsInitialized)
            _columnIndexBuffer = _writer.BufferWriters.CreateMetadataBufferWriter();
        if (!_offsetIndexBuffer.IsInitialized)
            _offsetIndexBuffer = _writer.BufferWriters.CreateMetadataBufferWriter();

        _columnIndexBuffer.Reset();
        ParquetMetadataThriftWriter.WriteColumnIndex(ref _columnIndexBuffer,
            metadata.PageIndex.Statistics.AsSpan(0, metadata.PageIndex.Count));
        metadata.ColumnIndexOffset = _writer.FileOffset;
        metadata.ColumnIndexLength = _columnIndexBuffer.WrittenLength;
        _writer.WriteBuffer(ref _columnIndexBuffer);

        _offsetIndexBuffer.Reset();
        ParquetMetadataThriftWriter.WriteOffsetIndex(ref _offsetIndexBuffer,
            metadata.PageIndex.Locations.AsSpan(0, metadata.PageIndex.Count));
        metadata.OffsetIndexOffset = _writer.FileOffset;
        metadata.OffsetIndexLength = _offsetIndexBuffer.WrittenLength;
        _writer.WriteBuffer(ref _offsetIndexBuffer);
    }
}
