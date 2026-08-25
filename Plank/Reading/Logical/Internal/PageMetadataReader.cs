using System.Runtime.CompilerServices;
using Plank.Reading.Internal;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

static class PageMetadataReader
{
    const int MaxPageHeaderLength = 64 * 1024;

    internal static ParquetDataPageMetadataCollection Open(ParquetFileReader reader, int rowGroupOrdinal,
        int physicalColumnOrdinal, LeafColumn definition, ulong rowCount)
        => new(OpenHandle(reader, rowGroupOrdinal, physicalColumnOrdinal, definition, rowCount));

    internal static PageMetadataHandle OpenHandle(ParquetFileReader reader, int rowGroupOrdinal,
        int physicalColumnOrdinal, LeafColumn definition, ulong rowCount)
    {
        var chunk = reader.Metadata.ColumnChunk(rowGroupOrdinal, physicalColumnOrdinal);
        var columnIndex = ReadSection(reader, chunk.ColumnIndexOffset, chunk.ColumnIndexLength, "column index");
        var offsetIndex = ReadSection(reader, chunk.OffsetIndexOffset, chunk.OffsetIndexLength, "offset index");
        ParquetBuffer entries = default;
        var statisticsBuilder = new StatisticsBufferBuilder(reader.BufferPool);
        try
        {
            var count = -1;
            var capacity = 0;
            var boundaryOrder = ParquetBoundaryOrder.Unknown;
            if (!columnIndex.IsEmpty)
                ParseColumnIndex(columnIndex.Span, reader.BufferPool, ref entries, ref capacity, ref count,
                    ref boundaryOrder);

            if (!offsetIndex.IsEmpty)
            {
                ParseOffsetIndex(offsetIndex.Span, reader.BufferPool, ref entries, ref capacity, ref count);
                ValidateLocations(reader, chunk, Entries(entries, count));
                if (columnIndex.IsEmpty)
                    PopulateHeaderStatistics(reader, chunk, definition, Entries(entries, count),
                        ref statisticsBuilder);
            }
            else
                ScanPageLocations(reader, chunk, definition, reader.BufferPool, ref entries, ref capacity, ref count,
                    columnIndex.IsEmpty, ref statisticsBuilder);

            DeriveRowCounts(Entries(entries, count), rowCount);
            var statisticsStorage = columnIndex.IsEmpty ? statisticsBuilder.Detach() : columnIndex;
            return new PageMetadataHandle(entries, count, statisticsStorage, definition, rowGroupOrdinal,
                boundaryOrder);
        }
        catch
        {
            entries.Dispose();
            statisticsBuilder.Dispose();
            columnIndex.Dispose();
            throw;
        }
        finally
        {
            offsetIndex.Dispose();
        }
    }

    static ParquetBuffer ReadSection(ParquetFileReader reader, ulong offset, uint length, string name)
    {
        if (length == 0)
            return default;
        if (offset > reader.Source.Length || length > reader.Source.Length - offset)
            throw new CorruptParquetException(
                $"The {name} at offset {offset} with length {length} exceeds the source length ({reader.Source.Length}).");

        var buffer = reader.BufferPool.Rent(length);
        try
        {
            reader.Source.ReadExactly(offset, buffer.Span[..checked((int)length)]);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    static void ParseColumnIndex(ReadOnlySpan<byte> bytes, IParquetBufferPool pool, ref ParquetBuffer entries,
        ref int capacity, ref int count, ref ParquetBoundaryOrder boundaryOrder)
    {
        var reader = new CompactProtocolReader(bytes);
        var nullPagesCount = -1;
        var minimumCount = -1;
        var maximumCount = -1;
        var nullCountsCount = -1;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                {
                    var (listCount, elementType) = reader.ReadListHeader();
                    if (elementType is not (CompactProtocolType.BooleanTrue or CompactProtocolType.BooleanFalse))
                        throw new CorruptParquetException("Column index null_pages must be a boolean list.");
                    var itemCount = ListCount(listCount, reader.Remaining, "Column index null_pages");
                    EnsureEntries(pool, ref entries, ref capacity, itemCount);
                    var destination = Entries(entries, itemCount);
                    for (var i = 0; i < itemCount; i++)
                    {
                        destination[i].HasNullPage = true;
                        destination[i].IsNullPage = reader.ReadBool(null);
                    }
                    nullPagesCount = itemCount;
                    count = MergeCount(count, itemCount, "column index");
                    break;
                }
                case 2:
                    minimumCount = ReadBounds(ref reader, bytes, pool, ref entries, ref capacity, ref count,
                        writeMinimum: true);
                    break;
                case 3:
                    maximumCount = ReadBounds(ref reader, bytes, pool, ref entries, ref capacity, ref count,
                        writeMinimum: false);
                    break;
                case 4:
                    boundaryOrder = reader.ReadI32() switch
                    {
                        0 => ParquetBoundaryOrder.Unordered,
                        1 => ParquetBoundaryOrder.Ascending,
                        2 => ParquetBoundaryOrder.Descending,
                        var value => throw new CorruptParquetException(
                            $"Column index boundary order '{value}' is invalid.")
                    };
                    break;
                case 5:
                {
                    var (listCount, elementType) = reader.ReadListHeader();
                    if (elementType != CompactProtocolType.I64)
                        throw new CorruptParquetException("Column index null_counts must be an i64 list.");
                    var itemCount = ListCount(listCount, reader.Remaining, "Column index null_counts");
                    EnsureEntries(pool, ref entries, ref capacity, itemCount);
                    var destination = Entries(entries, itemCount);
                    for (var i = 0; i < itemCount; i++)
                    {
                        var nullCount = reader.ReadI64();
                        if (nullCount < 0)
                            throw new CorruptParquetException("Page null count cannot be negative.");
                        var previous = destination[i].Statistics;
                        destination[i].Statistics = new EncodedStatistics(previous.MinimumOffset,
                            previous.MinimumLength, previous.MaximumOffset, previous.MaximumLength, nullCount,
                            previous.DistinctCount, previous.HasMinimum, previous.HasMaximum, hasNullCount: true,
                            previous.HasDistinctCount, previous.IsMinimumExact, previous.IsMaximumExact);
                    }
                    nullCountsCount = itemCount;
                    count = MergeCount(count, itemCount, "column index");
                    break;
                }
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        if (nullPagesCount < 0 || minimumCount < 0 || maximumCount < 0)
            throw new CorruptParquetException(
                "Column index must contain null_pages, min_values, and max_values.");
        if (nullCountsCount >= 0 && nullCountsCount != count)
            throw new CorruptParquetException("Column index null_counts length does not match its page count.");

        var parsed = Entries(entries, count);
        for (var i = 0; i < parsed.Length; i++)
            if (parsed[i].HasNullPage && parsed[i].IsNullPage)
            {
                var previous = parsed[i].Statistics;
                parsed[i].Statistics = new EncodedStatistics(0, 0, 0, 0, previous.NullCount,
                    previous.DistinctCount, hasMinimum: false, hasMaximum: false, previous.HasNullCount,
                    previous.HasDistinctCount, minimumExact: false, maximumExact: false);
            }
    }

    static int ReadBounds(ref CompactProtocolReader reader, ReadOnlySpan<byte> storage, IParquetBufferPool pool,
        ref ParquetBuffer entries, ref int capacity, ref int count, bool writeMinimum)
    {
        var (listCount, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Binary)
            throw new CorruptParquetException("Column index bounds must be a binary list.");
        var itemCount = ListCount(listCount, reader.Remaining, "Column index bounds");
        EnsureEntries(pool, ref entries, ref capacity, itemCount);
        var destination = Entries(entries, itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            var value = reader.ReadBinary();
            var offset = reader.Offset - value.Length;
            if ((uint)offset > (uint)storage.Length)
                throw new CorruptParquetException("Column index bound offset is invalid.");
            var previous = destination[i].Statistics;
            destination[i].Statistics = writeMinimum
                ? new EncodedStatistics(offset, value.Length, previous.MaximumOffset, previous.MaximumLength,
                    previous.NullCount, previous.DistinctCount, hasMinimum: true,
                    previous.HasMaximum, previous.HasNullCount, previous.HasDistinctCount,
                    minimumExact: false, previous.IsMaximumExact)
                : new EncodedStatistics(previous.MinimumOffset, previous.MinimumLength, offset, value.Length,
                    previous.NullCount, previous.DistinctCount, previous.HasMinimum,
                    hasMaximum: true, previous.HasNullCount, previous.HasDistinctCount,
                    previous.IsMinimumExact, maximumExact: false);
        }
        count = MergeCount(count, itemCount, "column index");
        return itemCount;
    }

    static void ParseOffsetIndex(ReadOnlySpan<byte> bytes, IParquetBufferPool pool, ref ParquetBuffer entries,
        ref int capacity, ref int count)
    {
        var reader = new CompactProtocolReader(bytes);
        var locationsCount = -1;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId != 1)
            {
                reader.Skip(type, inlineBool);
                continue;
            }

            var (listCount, elementType) = reader.ReadListHeader();
            if (elementType != CompactProtocolType.Struct)
                throw new CorruptParquetException("Offset index page_locations must be a struct list.");
            var itemCount = ListCount(listCount, reader.Remaining, "Offset index page_locations");
            EnsureEntries(pool, ref entries, ref capacity, itemCount);
            var destination = Entries(entries, itemCount);
            for (var i = 0; i < itemCount; i++)
            {
                var offset = 0UL;
                var compressedSize = 0U;
                var firstRowIndex = 0UL;
                var hasOffset = false;
                var hasCompressedSize = false;
                var hasFirstRowIndex = false;
                reader.BeginStruct();
                while (reader.TryReadFieldHeader(out var locationFieldId, out var locationType,
                           out var locationInlineBool))
                {
                    switch (locationFieldId)
                    {
                        case 1:
                            offset = reader.ReadI64AsU64();
                            hasOffset = true;
                            break;
                        case 2:
                            compressedSize = reader.ReadI32AsU32();
                            hasCompressedSize = true;
                            break;
                        case 3:
                            firstRowIndex = reader.ReadI64AsU64();
                            hasFirstRowIndex = true;
                            break;
                        default:
                            reader.Skip(locationType, locationInlineBool);
                            break;
                    }
                }

                if (!hasOffset || !hasCompressedSize || !hasFirstRowIndex)
                    throw new CorruptParquetException(
                        $"Offset index page location {i} is missing a required field.");

                destination[i].Offset = offset;
                destination[i].CompressedSize = compressedSize;
                destination[i].FirstRowIndex = firstRowIndex;
                destination[i].HasLocation = true;
                destination[i].HasFirstRowIndex = true;
            }
            locationsCount = itemCount;
            count = MergeCount(count, itemCount, "page indexes");
        }

        if (locationsCount < 0)
            throw new CorruptParquetException("Offset index is missing page_locations.");
    }

    static void ScanPageLocations(ParquetFileReader reader, ParquetColumnChunkInfo chunk, LeafColumn definition,
        IParquetBufferPool pool, ref ParquetBuffer entries, ref int capacity, ref int count,
        bool copyHeaderStatistics, ref StatisticsBufferBuilder statisticsBuilder)
    {
        if (chunk.TotalCompressedSize > int.MaxValue)
            throw new NotSupportedException("Column chunks larger than Int32.MaxValue are not supported.");
        if (chunk.ChunkOffset > reader.Source.Length ||
            chunk.TotalCompressedSize > reader.Source.Length - chunk.ChunkOffset)
            throw new CorruptParquetException("Column chunk exceeds the source length.");

        var chunkLength = checked((int)chunk.TotalCompressedSize);
        var offset = 0;
        var dataPageCount = 0;
        while (offset < chunkLength)
        {
            var header = ReadPageHeader(reader, chunk.ChunkOffset + (ulong)offset, chunkLength - offset,
                chunk.TotalUncompressedSize, copyHeaderStatistics, ref statisticsBuilder,
                out var headerStatistics);
            var pageSize = checked(header.HeaderLength + (int)header.CompressedPageSize);
            if (pageSize > chunkLength - offset)
                throw new CorruptParquetException("Page size exceeds the remaining column chunk.");

            if (header.Type is PageHeaderType.DataPage or PageHeaderType.DataPageV2)
            {
                EnsureEntries(pool, ref entries, ref capacity, dataPageCount + 1);
                ref var entry = ref Entries(entries, dataPageCount + 1)[dataPageCount];
                entry.Offset = chunk.ChunkOffset + (ulong)offset;
                entry.CompressedSize = checked((uint)pageSize);
                entry.HasLocation = true;
                if (copyHeaderStatistics)
                    entry.Statistics = headerStatistics;
                var pageRowCount = header.Type == PageHeaderType.DataPageV2
                    ? header.RowCount
                    : definition.Options.Repetition == ParquetRepetition.Repeated ? 0U : header.ValueCount;
                if (pageRowCount != 0)
                {
                    entry.RowCount = pageRowCount;
                    entry.HasRowCount = true;
                }
                if (copyHeaderStatistics && headerStatistics.HasNullCount &&
                    definition.Options.Repetition != ParquetRepetition.Repeated)
                {
                    entry.HasNullPage = true;
                    entry.IsNullPage = (ulong)headerStatistics.NullCount == header.ValueCount;
                }
                dataPageCount++;
            }

            offset += pageSize;
        }

        count = MergeCount(count, dataPageCount, "column index and scanned pages");
    }

    static void PopulateHeaderStatistics(ParquetFileReader reader, ParquetColumnChunkInfo chunk,
        LeafColumn definition, Span<PageMetadataEntry> entries, ref StatisticsBufferBuilder statisticsBuilder)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            if (entry.CompressedSize > int.MaxValue)
                throw new NotSupportedException("Pages larger than Int32.MaxValue are not supported.");
            var header = ReadPageHeader(reader, entry.Offset, checked((int)entry.CompressedSize),
                chunk.TotalUncompressedSize, copyStatistics: true, ref statisticsBuilder,
                out var statistics);
            if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2))
                throw new CorruptParquetException(
                    $"Offset index entry {i} points to page type '{header.Type}' instead of a data page.");
            entry.Statistics = statistics;
            if (!statistics.HasNullCount || definition.Options.Repetition == ParquetRepetition.Repeated)
                continue;
            entry.HasNullPage = true;
            entry.IsNullPage = (ulong)statistics.NullCount == header.ValueCount;
        }
    }

    static void ValidateLocations(ParquetFileReader reader, ParquetColumnChunkInfo chunk,
        ReadOnlySpan<PageMetadataEntry> entries)
    {
        if (chunk.ChunkOffset > reader.Source.Length ||
            chunk.TotalCompressedSize > reader.Source.Length - chunk.ChunkOffset)
            throw new CorruptParquetException("Column chunk exceeds the source length.");
        var chunkEnd = checked(chunk.ChunkOffset + chunk.TotalCompressedSize);
        var previousEnd = chunk.ChunkOffset;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (!entry.HasLocation || entry.CompressedSize == 0 ||
                entry.Offset < previousEnd || entry.Offset >= chunkEnd ||
                entry.CompressedSize > chunkEnd - entry.Offset)
                throw new CorruptParquetException(
                    $"Offset index page location {i} is outside its column chunk or overlaps an earlier page.");
            previousEnd = checked(entry.Offset + entry.CompressedSize);
        }
    }

    /// <summary>Reads the page header at <paramref name="offset"/> and nothing past it.</summary>
    /// <remarks>
    /// A page header does not carry its own length, so the window has to grow
    /// until one parses. It grows by exactly what the parse says it is short,
    /// which is what keeps this from reading page payload: a caller scanning a
    /// chunk's page metadata should not have to pull the chunk's data with it,
    /// and HeaderFallbackInspectionReadsOnlyHeaders holds it to that.
    ///
    /// The shortfall used to be inferred by comparing the parse failure's Message
    /// against a literal. That missed the message a half-read statistics min/max
    /// actually raises — a binary field reports the bound its length prefix broke,
    /// not the end of the payload — so the loop aborted on every page header with
    /// statistics in it. Plank's writer emits none, so nothing in the suite or the
    /// fuzz corpus had any; 26 files in apache/parquet-testing do. The signal is
    /// now structural: see CompactProtocolTruncatedException.
    /// </remarks>
    static PageHeader ReadPageHeader(ParquetFileReader reader, ulong offset, int remainingChunkLength,
        ulong totalUncompressedSize, bool copyStatistics, ref StatisticsBufferBuilder statisticsBuilder,
        out EncodedStatistics copiedStatistics)
    {
        var maxLength = Math.Min(remainingChunkLength, MaxPageHeaderLength);
        using var buffer = reader.BufferPool.Rent(checked((uint)maxLength));
        var maxUncompressedPageSize = (uint)Math.Min(totalUncompressedSize, uint.MaxValue);
        var length = 0;
        var missingBytes = 1;
        while (length < maxLength)
        {
            // One byte to start, because the first parse cannot know anything, and
            // afterwards exactly the shortfall the parse reported.
            var wanted = (int)Math.Min((long)length + Math.Max(missingBytes, 1), maxLength);
            reader.Source.ReadExactly(offset + (ulong)length, buffer.Span[length..wanted]);
            length = wanted;

            if (!PageHeaderReader.TryRead(buffer.Span[..length], maxUncompressedPageSize, out var header,
                    out missingBytes))
                continue;

            var statistics = header.Statistics;
            if (header.Type == PageHeaderType.DataPageV2 && !statistics.HasNullCount)
                statistics = WithNullCount(statistics, header.NullCount);

            // The offsets in EncodedStatistics are relative to the window that was
            // parsed, so that whole window is what Copy has to slice out of.
            copiedStatistics = copyStatistics
                ? statisticsBuilder.Copy(buffer.Span[..length], statistics)
                : default;
            return header;
        }

        throw new CorruptParquetException(
            $"Page header exceeds the supported maximum length of {MaxPageHeaderLength} bytes.");
    }

    static EncodedStatistics WithNullCount(EncodedStatistics statistics, uint nullCount)
        => new(statistics.MinimumOffset, statistics.MinimumLength, statistics.MaximumOffset,
            statistics.MaximumLength, nullCount, statistics.DistinctCount, statistics.HasMinimum,
            statistics.HasMaximum, hasNullCount: true, statistics.HasDistinctCount,
            statistics.IsMinimumExact, statistics.IsMaximumExact);

    static void DeriveRowCounts(Span<PageMetadataEntry> entries, ulong rowGroupRowCount)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            if (entry.HasFirstRowIndex)
            {
                var end = i + 1 < entries.Length && entries[i + 1].HasFirstRowIndex
                    ? entries[i + 1].FirstRowIndex
                    : rowGroupRowCount;
                if (entry.FirstRowIndex > end || end > rowGroupRowCount)
                    throw new CorruptParquetException("Offset index first_row_index values are not monotonic.");
                entry.RowCount = end - entry.FirstRowIndex;
                entry.HasRowCount = true;
                continue;
            }

            if (!entry.HasRowCount)
                continue;
            entry.FirstRowIndex = i == 0
                ? 0
                : entries[i - 1].HasFirstRowIndex && entries[i - 1].HasRowCount
                    ? checked(entries[i - 1].FirstRowIndex + entries[i - 1].RowCount)
                    : 0;
            entry.HasFirstRowIndex = i == 0 ||
                entries[i - 1].HasFirstRowIndex && entries[i - 1].HasRowCount;
        }
    }

    static int MergeCount(int current, int next, string structure)
    {
        if (current >= 0 && current != next)
            throw new CorruptParquetException(
                $"{structure} contains inconsistent page counts ({current} and {next}).");
        return next;
    }

    // Every one of these counts comes off a Thrift list header in the file, and
    // every list element costs at least one byte, so a count larger than what
    // is left cannot be satisfied. Checking here rather than casting keeps a
    // corrupt column or offset index from raising OverflowException at a
    // reader instead of the CorruptParquetException callers are told to catch,
    // and matches how PhysicalMetadataThriftReader bounds the footer's lists.
    static int ListCount(uint listCount, uint remaining, string structure)
    {
        if (listCount > remaining)
            throw new CorruptParquetException(
                $"{structure} declares {listCount} entries, which exceeds the {remaining} bytes remaining.");
        return (int)listCount;
    }

    static void EnsureEntries(IParquetBufferPool pool, ref ParquetBuffer buffer, ref int capacity, int required)
    {
        if (required <= capacity)
            return;
        // `required` is the page count declared by the column index, so it is
        // file-controlled: a corrupt one made both the growth doubling and the
        // byte-size multiply below overflow, surfacing as OverflowException
        // rather than the CorruptParquetException a reader is documented to
        // throw. Compute in long and reject over-large counts as what they are.
        var nextCapacity = Math.Max((long)required, Math.Max(8L, (long)capacity * 2));
        var byteLength = nextCapacity * Unsafe.SizeOf<PageMetadataEntry>();
        if ((ulong)byteLength > int.MaxValue)
            throw new CorruptParquetException(
                $"Column index declares {required} pages, which needs more than {int.MaxValue} bytes of page metadata.");

        var next = pool.Rent((uint)byteLength);
        var nextEntries = ParquetBuffer.AsSpan<PageMetadataEntry>(next, (int)nextCapacity);
        nextEntries.Clear();
        if (capacity != 0)
            Entries(buffer, capacity).CopyTo(nextEntries);
        buffer.Dispose();
        buffer = next;
        capacity = (int)nextCapacity;
    }

    static Span<PageMetadataEntry> Entries(ParquetBuffer buffer, int count)
        => count == 0 ? [] : ParquetBuffer.AsSpan<PageMetadataEntry>(buffer, count);
}
