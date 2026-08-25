using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class LogicalPageMetadataTests
{
    static readonly ParquetPagePruner _containsFour =
        static (in ParquetDataPageMetadata page) => ContainsInt32(in page, 4);

    static readonly ParquetPagePruner _rejectAll =
        static (in ParquetDataPageMetadata _) => false;

    static readonly ParquetPagePruner _throw =
        static (in ParquetDataPageMetadata _) => throw new InvalidOperationException("pruner failed");

    [Test]
    public void ExposesColumnAndPageStatistics()
    {
        var (schema, bytes) = CreateFile();
        using var reader = schema.CreateReader(new MemoryStream(bytes));
        var column = reader.RowGroups[0].Column<int>(schema.LeafColumns[0]);
        var metadata = column.Metadata;
        var statistics = metadata.Statistics;

        if (metadata.ValueCount != 6 ||
            !metadata.HasColumnIndex ||
            !metadata.HasOffsetIndex ||
            BinaryPrimitives.ReadInt32LittleEndian(statistics.Minimum) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(statistics.Maximum) != 6 ||
            statistics.NullCount != 0)
            throw new InvalidOperationException("Column metadata did not match the written statistics.");

        using var pages = metadata.OpenPages();
        if (pages.Count != 3 ||
            pages[0].FirstRowIndex != 0 ||
            pages[0].RowCount != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(pages[1].Statistics.Minimum) != 3 ||
            BinaryPrimitives.ReadInt32LittleEndian(pages[1].Statistics.Maximum) != 4)
            throw new InvalidOperationException("Page metadata did not match the written indexes.");

        var enumerated = 0;
        foreach (var page in pages)
        {
            if (page.PageOrdinal != enumerated)
                throw new InvalidOperationException("Page metadata enumeration returned an unexpected ordinal.");
            enumerated++;
        }
        if (enumerated != 3)
            throw new InvalidOperationException("Page metadata enumeration returned an unexpected count.");
    }

    /// <remarks>
    /// Plank's writer emits no page-header statistics, so every fixture and every
    /// fuzz seed in this repository had headers without them and the probe that
    /// reads a header back was never given one. Arrow writes them whenever
    /// statistics are on, and reaches the probe whenever the page index is off —
    /// with an offset index in the file the reader takes each page's bounds from
    /// it and never parses a header speculatively.
    /// </remarks>
    [Test]
    [Arguments(WriterKind.Int32)]
    [Arguments(WriterKind.String)]
    [Arguments(WriterKind.Double)]
    public async Task OpensPageHeadersCarryingStatisticsFromAnotherWriter(WriterKind kind)
    {
        var bytes = CreateArrowFileWithHeaderStatistics(kind);
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes, writable: false));

        using var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();

        await Assert.That(pages.Count).IsEqualTo(1);
        await Assert.That(pages[0].Statistics.Minimum.Length).IsGreaterThan(0);
        await Assert.That(pages[0].Statistics.Maximum.Length).IsGreaterThan(0);
    }

    /// <remarks>
    /// The metadata API is not the only way in. A pruner makes an ordinary read
    /// take the same path, because the enumerator has to know each page's bounds
    /// before it can decide to skip one.
    /// </remarks>
    [Test]
    public async Task PrunedReadOfAnotherWriterHeaderStatisticsReturnsEveryValue()
    {
        var bytes = CreateArrowFileWithHeaderStatistics(WriterKind.Int32);
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes, writable: false),
            static (in ParquetDataPageMetadata _) => true);

        var values = new List<int>();
        foreach (var buffer in reader.RowGroups[0].Column<int>(reader.Schema.LeafColumns[0]))
            values.AddRange(buffer.Values.ToArray());

        await Assert.That(values).IsEquivalentTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    public enum WriterKind
    {
        Int32,
        String,
        Double
    }

    // Statistics on and the page index off is the combination that produces a
    // header the reader has to parse without knowing its length, with a min and
    // a max inside it. Arrow writes a page index by default, which routes around
    // that path entirely.
    static byte[] CreateArrowFileWithHeaderStatistics(WriterKind kind)
    {
        using var stream = new MemoryStream();
        using var properties = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Uncompressed)
            .EnableStatistics()
            .DisableWritePageIndex()
            .Build();
        ParquetSharp.Column column = kind switch
        {
            WriterKind.Int32 => new ParquetSharp.Column<int>("value"),
            WriterKind.String => new ParquetSharp.Column<string?>("value"),
            _ => new ParquetSharp.Column<double>("value")
        };

        using (var writer = new ParquetSharp.ParquetFileWriter(stream, [column], null, properties, null,
            leaveOpen: true))
        {
            using (var rowGroup = writer.AppendRowGroup())
            using (var columnWriter = rowGroup.NextColumn())
                switch (kind)
                {
                    case WriterKind.Int32:
                        columnWriter.LogicalWriter<int>().WriteBatch([1, 2, 3, 4, 5, 6, 7, 8]);
                        break;
                    case WriterKind.String:
                        columnWriter.LogicalWriter<string?>()
                            .WriteBatch(["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"]);
                        break;
                    default:
                        columnWriter.LogicalWriter<double>().WriteBatch([1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5]);
                        break;
                }

            writer.Close();
        }

        return stream.ToArray();
    }

    [Test]
    public async Task ReaderLevelPrunerSkipsRejectedPages()
    {
        var (schema, bytes) = CreateFile();
        using var reader = schema.CreateReader(new MemoryStream(bytes), pagePruner: _containsFour);

        var values = ReadValues(reader.RowGroups[0].Column<int>(schema.LeafColumns[0]));

        await Assert.That(values).IsEquivalentTo([3, 4]);
    }

    [Test]
    public async Task ResetWithoutPrunerClearsPreviousPolicy()
    {
        var (schema, bytes) = CreateFile();
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes), _rejectAll);
        await Assert.That(ReadValues(reader.RowGroups[0].Column<int>(0))).IsEmpty();

        reader.Reset(new MemoryStream(bytes));

        await Assert.That(ReadValues(reader.RowGroups[0].Column<int>(0)))
            .IsEquivalentTo([1, 2, 3, 4, 5, 6]);
    }

    [Test]
    public void InspectionDoesNotReadColumnPayloads()
    {
        var (schema, bytes) = CreateFile();
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(new MemoryReadSource(bytes));
        var chunk = physicalReader.Metadata.ColumnChunk(0, 0);
        var source = new TrackingReadSource(bytes);
        using var reader = schema.CreateReader(source);
        source.Clear();

        using var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();
        for (var i = 0; i < pages.Count; i++)
            _ = pages[i].Statistics.Minimum.Length;

        source.AssertDoesNotOverlap(chunk.ChunkOffset, chunk.TotalCompressedSize,
            "Metadata inspection read bytes from the column chunk.");
    }

    [Test]
    public async Task PruningReadsOnlyAcceptedDataPages()
    {
        var (schema, bytes) = CreateFile();
        var pageOffsets = new ulong[3];
        var pageSizes = new uint[3];
        using (var planningReader = schema.CreateReader(new MemoryStream(bytes)))
        using (var pages = planningReader.RowGroups[0].GetColumnMetadata(0).OpenPages())
            for (var i = 0; i < pages.Count; i++)
            {
                pageOffsets[i] = pages[i].Offset;
                pageSizes[i] = pages[i].CompressedSize;
            }

        var source = new TrackingReadSource(bytes);
        using var reader = schema.CreateReader(source, pagePruner: _containsFour);
        source.Clear();

        var values = ReadValues(reader.RowGroups[0].Column<int>(0));

        await Assert.That(values).IsEquivalentTo([3, 4]);
        source.AssertDoesNotOverlap(pageOffsets[0], pageSizes[0],
            "Pruning read bytes from the first rejected data page.");
        source.AssertDoesNotOverlap(pageOffsets[2], pageSizes[2],
            "Pruning read bytes from the last rejected data page.");
    }

    [Test]
    public void RejectingEveryPageDoesNotLoadTheColumnChunk()
    {
        var (schema, bytes) = CreateFile();
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(new MemoryReadSource(bytes));
        var chunk = physicalReader.Metadata.ColumnChunk(0, 0);
        var source = new TrackingReadSource(bytes);
        using var reader = schema.CreateReader(source, pagePruner: _rejectAll);
        source.Clear();

        if (ReadValues(reader.RowGroups[0].Column<int>(0)).Length != 0)
            throw new InvalidOperationException("The reject-all pruner returned values.");

        source.AssertDoesNotOverlap(chunk.ChunkOffset, chunk.TotalCompressedSize,
            "Rejecting every data page still loaded the column chunk.");
    }

    [Test]
    public void HeaderFallbackExposesPagesWithoutIndexes()
    {
        var (schema, bytes) = CreateFile(writePageIndexes: false);
        using var reader = schema.CreateReader(new MemoryStream(bytes));
        var metadata = reader.RowGroups[0].GetColumnMetadata(schema.LeafColumns[0]);
        if (metadata.HasColumnIndex || metadata.HasOffsetIndex)
            throw new InvalidOperationException("The fallback fixture unexpectedly contains page indexes.");

        using var pages = metadata.OpenPages();
        if (pages.Count != 3 ||
            pages[0].Offset == 0 ||
            pages[0].RowCount != 2 ||
            pages[1].FirstRowIndex != 2 ||
            pages[2].FirstRowIndex != 4 ||
            pages[0].Statistics.NullCount != 0)
            throw new InvalidOperationException("Page-header fallback returned unexpected metadata.");
    }

    [Test]
    public void HeaderFallbackInspectionReadsOnlyHeaders()
    {
        var (schema, bytes) = CreateFile(writePageIndexes: false);
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(new MemoryReadSource(bytes));
        var chunk = physicalReader.Metadata.ColumnChunk(0, 0);
        var source = new TrackingReadSource(bytes);
        using var reader = schema.CreateReader(source);
        source.Clear();

        using var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();

        source.AssertChunkReadsAreHeaders(chunk.ChunkOffset, chunk.TotalCompressedSize, pages);
    }

    [Test]
    public void PageCollectionRejectsAccessAfterDisposal()
    {
        var (schema, bytes) = CreateFile();
        using var reader = schema.CreateReader(new MemoryStream(bytes));
        var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();
        pages.Dispose();

        try
        {
            _ = pages[0];
            throw new InvalidOperationException("Disposed page metadata remained accessible.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [Test]
    public void ResetInvalidatesColumnMetadataButNotAnOwnedPageCollection()
    {
        var (schema, bytes) = CreateFile();
        using var reader = schema.CreateReader(new MemoryStream(bytes));
        var metadata = reader.RowGroups[0].GetColumnMetadata(0);
        using var pages = metadata.OpenPages();

        reader.Reset(new MemoryStream(bytes));

        if (pages[0].PageOrdinal != 0)
            throw new InvalidOperationException("Reset invalidated an independently owned page collection.");
        try
        {
            _ = metadata.ValueCount;
            throw new InvalidOperationException("Reset did not invalidate the footer metadata view.");
        }
        catch (ArgumentException)
        {
        }
    }

    [Test]
    public void CorruptPageIndexIsRejected()
    {
        var (schema, bytes) = CreateFile();
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(new MemoryReadSource(bytes));
        var chunk = physicalReader.Metadata.ColumnChunk(0, 0);
        bytes[checked((int)chunk.ColumnIndexOffset)] = 0xff;
        using var reader = schema.CreateReader(new MemoryStream(bytes));

        try
        {
            using var _ = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();
            throw new InvalidOperationException("A corrupt column index was accepted.");
        }
        catch (CorruptParquetException)
        {
        }
    }

    [Test]
    public void PrunerExceptionsPropagateBeforePayloadReads()
    {
        var (schema, bytes) = CreateFile();
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(new MemoryReadSource(bytes));
        var chunk = physicalReader.Metadata.ColumnChunk(0, 0);
        var source = new TrackingReadSource(bytes);
        using var reader = schema.CreateReader(source, pagePruner: _throw);
        source.Clear();

        try
        {
            _ = ReadValues(reader.RowGroups[0].Column<int>(0));
            throw new InvalidOperationException("The page-pruner exception was swallowed.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "pruner failed")
        {
        }

        source.AssertDoesNotOverlap(chunk.ChunkOffset, chunk.TotalCompressedSize,
            "A callback exception caused column payload I/O.");
    }

    [Test]
    public void RejectingDictionaryDataPagesDoesNotLoadTheDictionary()
    {
        var (schema, bytes) = CreateDictionaryFile();
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(new MemoryReadSource(bytes));
        var chunk = physicalReader.Metadata.ColumnChunk(0, 0);
        if (chunk.DictionaryPageOffset == 0)
            throw new InvalidOperationException("The dictionary fixture has no dictionary page.");
        var source = new TrackingReadSource(bytes);
        using var reader = schema.CreateReader(source, pagePruner: _rejectAll);
        source.Clear();

        _ = ReadValues(reader.RowGroups[0].Column<int>(0));

        source.AssertDoesNotOverlap(chunk.ChunkOffset, chunk.TotalCompressedSize,
            "Rejecting every dictionary-encoded data page loaded the dictionary.");
    }

    [Test]
    public void NullPagesExposeUnknownBoundsConservatively()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 8,
            WritePageIndexes = true
        });
        var column = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
        column.Serialize([null, null, null, null, null, null]);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        using var reader = schema.CreateReader(new MemoryStream(stream.ToArray()));
        using var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();

        var foundNullPage = false;
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (page.IsNullPage != true)
                continue;
            foundNullPage = true;
            if (page.Statistics.HasMinimum ||
                page.Statistics.HasMaximum ||
                page.Statistics.NullCount != (long?)page.RowCount)
                throw new InvalidOperationException("Null-page statistics were not exposed conservatively.");
        }
        if (!foundNullPage)
            throw new InvalidOperationException("The all-null fixture produced no null page.");
    }

    static (ParquetSchema Schema, byte[] Bytes) CreateFile(bool writePageIndexes = true)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 8,
            WritePageIndexes = writePageIndexes
        });
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize([1, 2, 3, 4, 5, 6]);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return (schema, stream.ToArray());
    }

    static (ParquetSchema Schema, byte[] Bytes) CreateDictionaryFile()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 8,
            WritePageIndexes = true
        });
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize([1, 1, 2, 2, 1, 2]);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return (schema, stream.ToArray());
    }

    static bool ContainsInt32(in ParquetDataPageMetadata page, int value)
    {
        var statistics = page.Statistics;
        return !statistics.HasMinimum ||
            !statistics.HasMaximum ||
            statistics.Minimum.Length != sizeof(int) ||
            statistics.Maximum.Length != sizeof(int) ||
            BinaryPrimitives.ReadInt32LittleEndian(statistics.Minimum) <= value &&
            BinaryPrimitives.ReadInt32LittleEndian(statistics.Maximum) >= value;
    }

    static int[] ReadValues(RowGroupColumn<int> column)
    {
        var values = new List<int>();
        foreach (var buffer in column)
            values.AddRange(buffer.Values);
        return values.ToArray();
    }

    sealed class TrackingReadSource : IParquetReadSource
    {
        readonly byte[] _bytes;
        readonly List<ReadRange> _reads = [];

        internal TrackingReadSource(byte[] bytes)
            => _bytes = bytes;

        public ulong Length
            => (ulong)_bytes.Length;

        public void Dispose()
        {
        }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            if (offset > Length || offset > int.MaxValue || (ulong)destination.Length > Length - offset)
                throw new CorruptParquetException("Tracking source read exceeds its bytes.");
            _reads.Add(new ReadRange(offset, checked((uint)destination.Length)));
            _bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        internal void Clear()
            => _reads.Clear();

        internal void AssertDoesNotOverlap(ulong offset, ulong length, string message)
        {
            var end = checked(offset + length);
            for (var i = 0; i < _reads.Count; i++)
            {
                var read = _reads[i];
                var readEnd = checked(read.Offset + read.Length);
                if (read.Offset < end && offset < readEnd)
                    throw new InvalidOperationException(
                        $"{message} Read [{read.Offset}, {readEnd}) overlaps [{offset}, {end}).");
            }
        }

        internal void AssertChunkReadsAreHeaders(ulong chunkOffset, ulong chunkLength,
            ParquetDataPageMetadataCollection pages)
        {
            var chunkEnd = checked(chunkOffset + chunkLength);
            for (var readIndex = 0; readIndex < _reads.Count; readIndex++)
            {
                var read = _reads[readIndex];
                var readEnd = checked(read.Offset + read.Length);
                if (read.Offset >= chunkEnd || readEnd <= chunkOffset)
                    continue;

                var isHeaderByte = false;
                for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
                {
                    var page = pages[pageIndex];
                    var available = Math.Min(_bytes.Length - checked((int)page.Offset), 64 * 1024);
                    var header = PageHeaderReader.Read(
                        _bytes.AsSpan(checked((int)page.Offset), available));
                    if (read.Offset < page.Offset ||
                        readEnd > page.Offset + (ulong)header.HeaderLength)
                        continue;
                    isHeaderByte = true;
                    break;
                }
                if (!isHeaderByte)
                    throw new InvalidOperationException(
                        $"Header fallback read non-header byte range [{read.Offset}, {readEnd}).");
            }
        }

        readonly record struct ReadRange(ulong Offset, uint Length);
    }
}
