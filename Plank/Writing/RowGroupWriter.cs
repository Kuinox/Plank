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
    bool[][] _nullPagesByColumn;
    PageLocation[][] _pageLocationsByColumn;
    uint _nextColumnOrdinal;
    uint? _rowCount;
    Dictionary<int, RepeatedRowShape>? _mapKeyShapes;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _compressedContent = default;
        _compressionInput = default;
        _compressedValues = default;
        _pageStatisticsByColumn = writer.ColumnCount == 0 ? [] : new ColumnStatistics[writer.ColumnCount][];
        _nullPagesByColumn = writer.ColumnCount == 0 ? [] : new bool[writer.ColumnCount][];
        _pageLocationsByColumn = writer.ColumnCount == 0 ? [] : new PageLocation[writer.ColumnCount][];
        ResetForNewRowGroup();
    }

    internal void ResetForNewRowGroup()
    {
        _nextColumnOrdinal = 0;
        _rowCount = null;
        _mapKeyShapes?.Clear();
    }

    public SerializedColumn<T> CreateSerializedColumn<T>(LeafColumn column)
        => _writer.CreateSerializedColumn<T>(column);

    public void Write<T>(SerializedColumn<T> serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        if (!ReferenceEquals(serialized._owner, _writer))
            throw new InvalidOperationException("SerializedColumn belongs to another ParquetWriter.");

        ISerializedColumn state = serialized;
        if (!state.HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn has no serialized data. Call serialized.Serialize(values) before Write(...).");

        if (_nextColumnOrdinal == (uint)_writer.ColumnCount)
            throw new InvalidOperationException(
                "All columns for this row group have already been written. Call writer.StartRowGroup() to start a new row group.");

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
        ValidateMapRowShapes(serialized.MapRowShapes, columnOrdinal);
        var column = _writer.ColumnsByOrdinal[columnOrdinal];
        var pages = state.Pages;
        var compression = _writer.ColumnCompressionsByOrdinal[columnOrdinal];
        if (compression.Kind != CompressionKind.None && !_compressedContent.IsInitialized)
            _compressedContent = _writer.BufferWriters.CreatePageBufferWriter();
        if (compression.Kind != CompressionKind.None && !_compressionInput.IsInitialized)
            _compressionInput = _writer.BufferWriters.CreatePageBufferWriter();
        if (compression.Kind != CompressionKind.None && !_compressedValues.IsInitialized)
            _compressedValues = _writer.BufferWriters.CreatePageBufferWriter();
        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        long valueCount = 0;
        var dataPageOffset = -1L;
        var dictionaryPageOffset = 0L;
        var hasDictionaryPage = false;
        var nullCount = 0L;
        var writePageIndexes = _writer.WritePageIndexes;
        if (writePageIndexes)
            EnsurePageIndexCapacity(columnOrdinal, pages.Count);
        var pageStatistics = writePageIndexes ? _pageStatisticsByColumn[columnOrdinal] : [];
        var nullPages = writePageIndexes ? _nullPagesByColumn[columnOrdinal] : [];
        var pageLocations = writePageIndexes ? _pageLocationsByColumn[columnOrdinal] : [];
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
                    if (compression.Kind != CompressionKind.None && pageContentSize > 0)
                    {
                        Plank.Writing.Compression.Compression.Compress(compression.Kind, compression.Level,
                            _writer.CompressionContext, ref page.Content, ref _compressedContent);
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
                    valueCount = checked(valueCount + dataPageValueCount);
                    var dataPageNullCount = page.NullCount;
                    nullCount = checked(nullCount + dataPageNullCount);
                    var repetitionLevelsByteLength = page.RepetitionLevelsByteLength;
                    var definitionLevelsByteLength = page.DefinitionLevelsByteLength;
                    var pageEncoding = page.Encoding;
                    var levelBytes = checked(repetitionLevelsByteLength + definitionLevelsByteLength);
                    if (levelBytes > (uint)pageContentSize)
                        throw new InvalidOperationException(
                            $"Invalid level byte lengths ({levelBytes}) for data page content size {pageContentSize}.");
                    var levelBytesInt32 = checked((int)levelBytes);
                    var valueBytes = pageContentSize - levelBytesInt32;
                    uncompressedPageHeaderSize = pageContentSize;
                    compressedContentSize = pageContentSize;
                    storedContentSize = pageContentSize;

                    if (compression.Kind != CompressionKind.None && valueBytes > 0)
                    {
                        if (levelBytes == 0)
                        {
                            Plank.Writing.Compression.Compression.Compress(compression.Kind, compression.Level,
                                _writer.CompressionContext, ref page.Content, ref _compressedContent);
                            compressedContentSize = _compressedContent.WrittenLength;
                        }
                        else
                        {
                            _compressionInput.Reset();
                            _compressedValues.Reset();
                            _compressedContent.Reset();

                            var source = _writer.CompressionContext.GetContiguousSourceSpan(ref page.Content);
                            var levels = source[..levelBytesInt32];
                            var values = source[levelBytesInt32..];
                            _compressionInput.Write(values);
                            Plank.Writing.Compression.Compression.Compress(compression.Kind, compression.Level,
                                _writer.CompressionContext, ref _compressionInput, ref _compressedValues);
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
            if (writePageIndexes)
            {
                pageStatistics[dataPageCount] = page.Statistics.HasStatistics
                    ? page.Statistics.WithNullCount(page.NullCount)
                    : ColumnStatistics.Empty(page.NullCount);
                nullPages[dataPageCount] = page.NullCount == page.ValueCount;
                pageLocations[dataPageCount] = new PageLocation(pageOffset, checked((uint)(headerSize + storedContentSize)),
                    firstRowIndex);
            }
            dataPageCount++;
            firstRowIndex = checked(firstRowIndex + page.RowCount);
        }

        if (dataPageOffset < 0)
            dataPageOffset = hasDictionaryPage ? dictionaryPageOffset : _writer.FileOffset;

        ref var columnMetadata = ref _writer.OpenRowGroupColumnMetadata[columnOrdinal];
        columnMetadata.DataPageOffset = dataPageOffset;
        columnMetadata.DictionaryPageOffset = dictionaryPageOffset;
        columnMetadata.ValueCount = valueCount;
        columnMetadata.TotalUncompressedSize = totalUncompressedSize;
        columnMetadata.TotalCompressedSize = totalCompressedSize;
        columnMetadata.DataEncoding = dataEncoding;
        columnMetadata.Compression = compression.Kind;
        columnMetadata.Statistics = state.Statistics.HasStatistics
            ? state.Statistics.WithNullCount(nullCount)
            : ColumnStatistics.Empty(nullCount);
        columnMetadata.HasDictionaryPage = hasDictionaryPage;
        columnMetadata.ColumnIndexOffset = 0;
        columnMetadata.ColumnIndexLength = 0;
        columnMetadata.OffsetIndexOffset = 0;
        columnMetadata.OffsetIndexLength = 0;
        columnMetadata.PageIndex = writePageIndexes
            ? new PageIndex(pageStatistics, nullPages, pageLocations, dataPageCount)
            : default;

        state.Consume();
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal != (uint)_writer.ColumnCount)
            return;

        if (writePageIndexes)
            WritePageIndexes(_writer.OpenRowGroupColumnMetadata);
        ParquetMetadataThriftWriter.WriteRowGroup(ref _writer.SerializedRowGroupsMetadata, _writer.ColumnsByOrdinal,
            _writer.ColumnPathsByOrdinal, _writer.OpenRowGroupColumnMetadata, _rowCount.GetValueOrDefault());
        _writer.CompleteOpenRowGroup(_rowCount.GetValueOrDefault());
        _mapKeyShapes?.Clear();
    }

    void ValidateMapRowShapes(RepeatedRowShape[]? shapes, int columnOrdinal)
    {
        var mapProjections = _writer.ColumnProjectionInfosByOrdinal[columnOrdinal].MapProjections;
        if (mapProjections.IsDefaultOrEmpty)
            return;
        if (shapes is null || shapes.Length != mapProjections.Length)
            throw new InvalidOperationException("Map serialization did not retain its row shapes.");

        var path = _writer.ColumnPathsByOrdinal[columnOrdinal];
        var mapKeyShapes = _mapKeyShapes ??= [];
        for (var i = 0; i < mapProjections.Length; i++)
        {
            var mapProjection = mapProjections[i];
            var shape = shapes[i];
            if (!mapKeyShapes.TryGetValue(mapProjection.Id, out var expected))
            {
                if (!mapProjection.IsKey)
                    throw new InvalidOperationException("Map value serialization did not follow its key serialization.");
                mapKeyShapes.Add(mapProjection.Id, shape);
                continue;
            }

            ValidateMapRowShape(expected, shape, path, mapProjection.PathLength, mapProjection.IsKey);
        }
    }

    static void ValidateMapRowShape(RepeatedRowShape expectedShape, RepeatedRowShape actualShape, string[] path,
        int mapPathLength, bool isKey)
    {
        var expectedOffsets = expectedShape.RowOffsets!;
        var expectedTokens = expectedShape.Tokens!;
        var actualOffsets = actualShape.RowOffsets!;
        var actualTokens = actualShape.Tokens!;
        if (expectedOffsets.Length != actualOffsets.Length)
            throw new InvalidOperationException("Map key and value columns have different row counts.");

        for (var rowIndex = 0; rowIndex < expectedOffsets.Length - 1; rowIndex++)
        {
            var expected = expectedTokens.AsSpan(expectedOffsets[rowIndex],
                expectedOffsets[rowIndex + 1] - expectedOffsets[rowIndex]);
            var actual = actualTokens.AsSpan(actualOffsets[rowIndex],
                actualOffsets[rowIndex + 1] - actualOffsets[rowIndex]);
            if (!expected.SequenceEqual(actual))
            {
                var columns = isKey ? "key columns" : "key and value columns";
                var mapPath = string.Join('.', path.AsSpan(0, mapPathLength).ToArray());
                throw new InvalidOperationException(
                    $"Map {columns} have different cardinalities at row {rowIndex} for '{mapPath}'.");
            }
        }
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
        var nullPages = _nullPagesByColumn[columnOrdinal];
        if (nullPages is null || nullPages.Length < pageCount)
        {
            Array.Resize(ref nullPages, pageCount);
            _nullPagesByColumn[columnOrdinal] = nullPages;
        }

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
        var statistics = metadata.PageIndex.Statistics.AsSpan(0, metadata.PageIndex.Count);
        var nullPages = metadata.PageIndex.NullPages.AsSpan(0, metadata.PageIndex.Count);
        if (CanWriteColumnIndex(statistics, nullPages))
        {
            ParquetMetadataThriftWriter.WriteColumnIndex(ref _columnIndexBuffer, statistics, nullPages);
            metadata.ColumnIndexOffset = _writer.FileOffset;
            metadata.ColumnIndexLength = checked((uint)_columnIndexBuffer.WrittenLength);
            _writer.WriteBuffer(ref _columnIndexBuffer);
        }

        _offsetIndexBuffer.Reset();
        ParquetMetadataThriftWriter.WriteOffsetIndex(ref _offsetIndexBuffer,
            metadata.PageIndex.Locations.AsSpan(0, metadata.PageIndex.Count));
        metadata.OffsetIndexOffset = _writer.FileOffset;
        metadata.OffsetIndexLength = checked((uint)_offsetIndexBuffer.WrittenLength);
        _writer.WriteBuffer(ref _offsetIndexBuffer);
    }

    static bool CanWriteColumnIndex(ReadOnlySpan<ColumnStatistics> statistics, ReadOnlySpan<bool> nullPages)
    {
        for (var i = 0; i < statistics.Length; i++)
            if (!nullPages[i] && statistics[i].ValueKind == ColumnStatistics.ColumnStatisticsValueKind.None)
                return false;

        return true;
    }

    internal void ReleaseBuffers()
    {
        _compressedContent.Dispose();
        _compressionInput.Dispose();
        _compressedValues.Dispose();
        _columnIndexBuffer.Dispose();
        _offsetIndexBuffer.Dispose();
    }
}
