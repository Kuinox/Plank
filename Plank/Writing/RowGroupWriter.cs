using Plank.Schema;
using Plank.Writing.Encoding;
using Plank.Writing.Thrift;

namespace Plank.Writing;

public sealed class RowGroupWriter
{
    readonly ParquetWriter _writer;
    BufferWriter _columnIndexBuffer;
    BufferWriter _offsetIndexBuffer;
    BufferWriter _bloomFilterHeaderBuffer;
    ColumnStatistics[][] _pageStatisticsByColumn;
    bool[][] _nullPagesByColumn;
    PageLocation[][] _pageLocationsByColumn;
    readonly ISerializedColumn?[] _serializedColumnsByOrdinal;
    uint _nextColumnOrdinal;
    uint? _rowCount;
    Dictionary<int, RepeatedRowShape>? _mapKeyShapes;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _pageStatisticsByColumn = writer.ColumnCount == 0 ? [] : new ColumnStatistics[writer.ColumnCount][];
        _nullPagesByColumn = writer.ColumnCount == 0 ? [] : new bool[writer.ColumnCount][];
        _pageLocationsByColumn = writer.ColumnCount == 0 ? [] : new PageLocation[writer.ColumnCount][];
        _serializedColumnsByOrdinal = HasBloomFilters(writer.ColumnsByOrdinal)
            ? new ISerializedColumn?[writer.ColumnCount]
            : [];
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
            var storedContentSize = page.Content.WrittenLength;

            switch (pageKind)
            {
                case PageKind.Dictionary:
                {
                    if (!hasDictionaryPage)
                    {
                        hasDictionaryPage = true;
                        dictionaryPageOffset = pageOffset;
                    }
                    break;
                }
                case PageKind.DataV1:
                case PageKind.DataV2:
                {
                    valueCount = checked(valueCount + page.ValueCount);
                    nullCount = checked(nullCount + page.NullCount);

                    if (dataPageOffset < 0)
                    {
                        dataPageOffset = pageOffset;
                        dataEncoding = page.Encoding;
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown page kind '{pageKind}'.");
            }

            var headerSize = page.Header.WrittenLength;
            _writer.WriteBuffer(ref page.Header);
            _writer.WriteBuffer(ref page.Content);

            totalUncompressedSize += checked((long)headerSize + page.UncompressedContentSize);
            totalCompressedSize += checked((long)headerSize + storedContentSize);
            if (pageKind == PageKind.Dictionary)
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
        columnMetadata.BloomFilterOffset = 0;
        columnMetadata.BloomFilterLength = 0;
        columnMetadata.PageIndex = writePageIndexes
            ? new PageIndex(pageStatistics, nullPages, pageLocations, dataPageCount)
            : default;

        if (!state.BloomFilterBitset.IsEmpty)
            _serializedColumnsByOrdinal[columnOrdinal] = state;
        state.Consume();
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal != (uint)_writer.ColumnCount)
            return;

        WriteBloomFilters(_writer.OpenRowGroupColumnMetadata);
        if (writePageIndexes)
            WritePageIndexes(_writer.OpenRowGroupColumnMetadata);
        ParquetMetadataThriftWriter.WriteRowGroup(ref _writer.SerializedRowGroupsMetadata, _writer.ColumnsByOrdinal,
            _writer.ColumnPathsByOrdinal, _writer.OpenRowGroupColumnMetadata, _writer.SortingColumns,
            _rowCount.GetValueOrDefault());
        _writer.CompleteOpenRowGroup(_rowCount.GetValueOrDefault());
        Array.Clear(_serializedColumnsByOrdinal);
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

    void WriteBloomFilters(Span<ColumnChunkMetadata> metadata)
    {
        if (_serializedColumnsByOrdinal.Length == 0)
            return;

        for (var i = 0; i < metadata.Length; i++)
        {
            if (_writer.ColumnsByOrdinal[i].Options.BloomFilter is null)
                continue;
            var serialized = _serializedColumnsByOrdinal[i]
                ?? throw new InvalidOperationException($"Column {i} did not retain its Bloom-filter state.");
            var bitset = serialized.BloomFilterBitset;

            if (!_bloomFilterHeaderBuffer.IsInitialized)
                _bloomFilterHeaderBuffer = _writer.BufferWriters.CreateMetadataBufferWriter();
            _bloomFilterHeaderBuffer.Reset();
            ParquetMetadataThriftWriter.WriteBloomFilterHeader(ref _bloomFilterHeaderBuffer, bitset.Length);

            ref var chunk = ref metadata[i];
            chunk.BloomFilterOffset = _writer.FileOffset;
            chunk.BloomFilterLength = checked((uint)(_bloomFilterHeaderBuffer.WrittenLength + bitset.Length));
            _writer.WriteBuffer(ref _bloomFilterHeaderBuffer);
            _writer.WriteBytes(bitset);
            serialized.CompleteBloomFilterWrite();
            _serializedColumnsByOrdinal[i] = null;
        }
    }

    static bool HasBloomFilters(Column[] columns)
    {
        for (var i = 0; i < columns.Length; i++)
            if (columns[i].Options.BloomFilter is not null)
                return true;
        return false;
    }

    internal void ReleaseBuffers()
    {
        _columnIndexBuffer.Dispose();
        _offsetIndexBuffer.Dispose();
        _bloomFilterHeaderBuffer.Dispose();
    }
}
