using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class LargePageHeaderTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1, CompressionKind.None, false)]
    [Arguments(ParquetDataPageVersion.V2, CompressionKind.None, false)]
    [Arguments(ParquetDataPageVersion.V1, CompressionKind.Gzip, true)]
    [Arguments(ParquetDataPageVersion.V2, CompressionKind.Gzip, true)]
    public void LargeBinaryStatisticsRoundTrip(ParquetDataPageVersion version, CompressionKind compression,
        bool prune)
    {
        var (schema, file, values) = CreateFile(version, compression);
        using var reader = schema.CreateReader(new MemoryStream(file, writable: false),
            pagePruner: prune ? static (in ParquetDataPageMetadata _) => true : null);
        var row = 0;
        foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
            for (var i = 0; i < buffer.Count; i++, row++)
                if (!buffer.GetValue(i).SequenceEqual(values[row]))
                    throw new InvalidOperationException($"Binary value {row} did not round-trip.");
        if (row != values.Length)
            throw new InvalidOperationException($"Expected {values.Length} rows, got {row}.");
    }

    [Test]
    public void BorrowedLargeHeaderAndPayloadDoNotRentBuffers()
    {
        var (_, file, _) = CreateFile();
        var pool = new TrackingBufferPool();
        using var source = new MemoryReadSource(file);
        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { BufferPool = pool });
        reader.Reset(source);
        pool.Reset();
        using var pages = reader.OpenPages(0, 0);

        if (!pages.MoveNext() || pages.CurrentHeader.HeaderLength <= 64 * 1024)
            throw new InvalidOperationException("Expected a page header larger than 64 KiB.");
        if (pool.RentCount != 0)
            throw new InvalidOperationException("Reading a borrowed large page unexpectedly rented storage.");
    }

    [Test]
    public void LargeHeaderMetadataPreservesStatisticsAndReadsNoPayload()
    {
        var (schema, file, values) = CreateFile();
        using var physicalSource = new MemoryReadSource(file);
        using var physical = new ParquetFileReader();
        physical.Reset(physicalSource);
        var chunk = physical.Metadata.ColumnChunk(0, 0);
        var header = PageHeaderReader.Read(file.AsSpan(checked((int)chunk.DataPageOffset)));
        using var source = new TrackingReadSource(file);
        using var reader = schema.CreateReader(source);
        source.Reads.Clear();

        using var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();
        if (pages.Count != 1 || !pages[0].Statistics.Minimum.SequenceEqual(values[0]) ||
            !pages[0].Statistics.Maximum.SequenceEqual(values[^1]))
            throw new InvalidOperationException("Large page-header statistics did not survive buffer growth.");
        foreach (var (offset, length) in source.Reads)
            if (offset < chunk.ChunkOffset || offset + (ulong)length > chunk.DataPageOffset + (ulong)header.HeaderLength)
                throw new InvalidOperationException("Metadata inspection read bytes outside the page header.");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public void InvalidBinaryLengthsAreRejectedBeforeGrowingStorage(bool metadata, bool exceedsInt32)
    {
        var (schema, file, _) = CreateFile(valueLength: 16);
        using var physicalSource = new MemoryReadSource(file);
        using var physical = new ParquetFileReader();
        physical.Reset(physicalSource);
        var chunk = physical.Metadata.ColumnChunk(0, 0);
        // Unknown binary field 9 with an impossible declared length. It must not drive a huge rent.
        byte[] prefix = exceedsInt32
            ? [0x98, 0xff, 0xff, 0xff, 0xff, 0x0f]
            : [0x98, 0xff, 0xff, 0xff, 0xff, 0x07];
        prefix.CopyTo(file.AsSpan(checked((int)chunk.DataPageOffset)));
        var pool = new TrackingBufferPool();
        using var input = new MemoryStream(file, writable: false);

        if (metadata)
        {
            using var reader = schema.CreateReader(input, new ParquetReaderOptions { BufferPool = pool });
            pool.Reset();
            Assert.Throws<CorruptParquetException>(() => reader.RowGroups[0].GetColumnMetadata(0).OpenPages());
        }
        else
        {
            using var reader = new ParquetFileReader(new ParquetFileReaderOptions { BufferPool = pool });
            reader.Reset(input);
            pool.Reset();
            using var pages = reader.OpenPages(0, 0);
            Assert.Throws<CorruptParquetException>(() => pages.MoveNext());
        }
        if (pool.LargestRent > chunk.TotalCompressedSize)
            throw new InvalidOperationException("A corrupt length requested storage larger than the column chunk.");
    }

    [Test]
    public void LongFormFieldsDoNotCauseQuadraticHeaderProbing()
    {
        var (schema, file, _) = CreateFile(valueLength: 1000);
        using var physical = new ParquetFileReader();
        physical.Reset(new MemoryReadSource(file));
        var chunk = physical.Metadata.ColumnChunk(0, 0);
        // Repeated long-form field 9, inline true: field IDs stay within i16,
        // but the header never terminates before the end of the chunk.
        var bytes = file.AsSpan(checked((int)chunk.ChunkOffset), checked((int)chunk.TotalCompressedSize));
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (i & 1) == 0 ? (byte)0x01 : (byte)0x12;
        using var source = new TrackingReadSource(file);
        using var reader = schema.CreateReader(source);
        source.Reads.Clear();

        Assert.Throws<CorruptParquetException>(() => reader.RowGroups[0].GetColumnMetadata(0).OpenPages());
        if (source.Reads.Count > 160)
            throw new InvalidOperationException($"Header probe performed {source.Reads.Count} reads.");
        foreach (var (offset, length) in source.Reads)
            if (offset < chunk.ChunkOffset || offset + (ulong)length > chunk.ChunkOffset + chunk.TotalCompressedSize)
                throw new InvalidOperationException("Header probe read outside the column chunk.");
    }

    [Test]
    public void LongHeaderReadAheadPreservesPageLocation()
    {
        var (schema, file, _) = CreateFile(valueLength: 1000);
        using var physical = new ParquetFileReader();
        physical.Reset(new MemoryReadSource(file));
        var chunk = physical.Metadata.ColumnChunk(0, 0);
        var bytes = file.AsSpan(checked((int)chunk.ChunkOffset), checked((int)chunk.TotalCompressedSize));
        const int fieldCount = 512;
        const int headerLength = 10 + 2 * fieldCount + 1;
        var payloadLength = bytes.Length - headerLength;
        var position = 0;
        bytes[position++] = 0x15; // type: data page
        bytes[position++] = 0;
        bytes[position++] = 0x15; // uncompressed_page_size
        WriteSize(bytes, ref position, payloadLength);
        bytes[position++] = 0x15; // compressed_page_size
        WriteSize(bytes, ref position, payloadLength);
        for (var i = 0; i < fieldCount; i++)
        {
            bytes[position++] = 0x01;
            bytes[position++] = 0x12;
        }
        bytes[position++] = 0; // end of PageHeader
        if (position != headerLength)
            throw new InvalidOperationException("Unexpected fixture header length.");

        using var source = new TrackingReadSource(file);
        using var reader = schema.CreateReader(source);
        source.Reads.Clear();
        using var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();
        if (pages.Count != 1 || pages[0].Offset != chunk.ChunkOffset ||
            pages[0].CompressedSize != chunk.TotalCompressedSize)
            throw new InvalidOperationException("Read-ahead changed the scanned page location.");
        var bytesRead = source.Reads.Sum(read => read.Length);
        if (bytesRead <= headerLength || bytesRead > 2 * headerLength || source.Reads.Count > 160)
            throw new InvalidOperationException($"Unexpected header read-ahead: {bytesRead} bytes.");

        static void WriteSize(Span<byte> destination, ref int offset, int value)
        {
            var encoded = (uint)value * 2;
            for (var i = 0; i < 3; i++)
            {
                destination[offset++] = (byte)((encoded & 0x7f) | (i < 2 ? 0x80U : 0));
                encoded >>= 7;
            }
            if (encoded != 0)
                throw new InvalidOperationException("Fixture size exceeds three-byte varint.");
        }
    }

    [Test]
    public void TruncatedHeaderCannotGrowPastItsPageBoundary()
    {
        var (_, file, _) = CreateFile();
        using var source = new MemoryReadSource(file);
        using var reader = new ParquetFileReader();
        reader.Reset(source);
        var offset = checked((int)reader.Metadata.ColumnChunk(0, 0).DataPageOffset);
        var complete = PageHeaderReader.Read(file.AsSpan(offset));
        var prefix = file.AsSpan(offset, complete.HeaderLength - 1);
        if (PageHeaderReader.TryRead(prefix, uint.MaxValue, out _, out var missingBytes))
            throw new InvalidOperationException("A truncated header was accepted.");
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.GetRequiredBufferLength(
            complete.HeaderLength - 1, missingBytes, complete.HeaderLength - 1));
    }

    static (ParquetSchema Schema, byte[] File, byte[][] Values) CreateFile(
        ParquetDataPageVersion version = ParquetDataPageVersion.V2,
        CompressionKind compression = CompressionKind.None, int valueLength = 20_000)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var values = Enumerable.Range(0, 20)
            .Select(index => Enumerable.Repeat((byte)index, valueLength).ToArray()).ToArray();
        using var stream = new MemoryStream();
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            DataPageVersion = version,
            Compression = compression,
            WritePageIndexes = false,
            TargetDataPageSizeBytes = 1024 * 1024
        });
        var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return (schema, stream.ToArray(), values);
    }

    sealed class TrackingBufferPool : IParquetBufferPool
    {
        internal int RentCount { get; private set; }
        internal uint LargestRent { get; private set; }

        public ParquetBuffer Rent(uint minimumByteLength)
        {
            RentCount++;
            LargestRent = Math.Max(LargestRent, minimumByteLength);
            return DefaultParquetBufferPool.Shared.Rent(minimumByteLength);
        }

        internal void Reset()
        {
            RentCount = 0;
            LargestRent = 0;
        }
    }

    sealed class TrackingReadSource(byte[] bytes) : IParquetReadSource
    {
        internal List<(ulong Offset, int Length)> Reads { get; } = [];
        public ulong Length => (ulong)bytes.Length;

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            Reads.Add((offset, destination.Length));
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        public void Dispose()
        {
        }
    }
}
