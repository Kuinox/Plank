using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

[ParquetSchema]
public sealed partial class BorrowedUtf8RowSchema
{
    [ParquetColumn("Plain", LogicalType = LogicalTypeKind.String,
        Encodings = [EncodingKind.Plain])]
    public ReadOnlyMemory<byte>? Plain { get; set; }

    [ParquetColumn("Dictionary", LogicalType = LogicalTypeKind.String,
        Encodings = [EncodingKind.RleDictionary])]
    public ReadOnlyMemory<byte>? Dictionary { get; set; }

    [ParquetColumn("DeltaLength", LogicalType = LogicalTypeKind.String,
        Encodings = [EncodingKind.DeltaLengthByteArray])]
    public ReadOnlyMemory<byte>? DeltaLength { get; set; }

    [ParquetColumn("DeltaByte", LogicalType = LogicalTypeKind.String,
        Encodings = [EncodingKind.DeltaByteArray])]
    public ReadOnlyMemory<byte>? DeltaByte { get; set; }
}

internal sealed class MemoryBackedPageBorrowingTests
{
    [Test]
    public void ArrayBackedSourceBorrowsUncompressedPageWithoutRenting()
    {
        var (_, file, _) = CreateFile();
        var pool = new TrackingBufferPool();
        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { BufferPool = pool });
        reader.Reset(new MemoryReadSource(file));
        var chunk = reader.Metadata.ColumnChunk(0, 0);
        pool.Reset();

        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");

        var payload = pages.CurrentPayload;
        var payloadOffset = checked((int)chunk.DataPageOffset + pages.CurrentHeader.HeaderLength);
        if (!payload.SequenceEqual(file.AsSpan(payloadOffset, payload.Length)))
            throw new InvalidOperationException("Borrowed page does not match the stored payload.");
        if (pool.RentCount != 0)
            throw new InvalidOperationException($"Borrowed page read unexpectedly rented {pool.RentCount} buffers.");
    }

    [Test]
    public void ArraySegmentSourceBorrowsFromItsOwnRange()
    {
        var (_, file, _) = CreateFile();
        const int prefixLength = 31;
        var storage = new byte[prefixLength + file.Length + 17];
        file.CopyTo(storage, prefixLength);
        var pool = new TrackingBufferPool();
        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { BufferPool = pool });
        reader.Reset(new MemoryReadSource(storage.AsMemory(prefixLength, file.Length)));
        pool.Reset();

        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext() || pages.CurrentPayload.IsEmpty)
            throw new InvalidOperationException("Expected a non-empty data page.");
        if (pool.RentCount != 0)
            throw new InvalidOperationException($"Array-segment page read unexpectedly rented {pool.RentCount} buffers.");
    }

    [Test]
    public void BorrowedPagesDecodeMultiplePagesCorrectly()
    {
        var (schema, file, expected) = CreateFile(valueCount: 4_096, targetPageSize: 256);
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var actualOffset = 0;

        foreach (var buffer in reader.RowGroups[0].Column<int>(0))
        {
            if (!buffer.Values.SequenceEqual(expected.AsSpan(actualOffset, buffer.Count)))
                throw new InvalidOperationException($"Decoded values differ at offset {actualOffset}.");
            actualOffset += buffer.Count;
        }

        if (actualOffset != expected.Length)
            throw new InvalidOperationException($"Decoded {actualOffset} values instead of {expected.Length}.");
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void GeneratedUtf8RowsBorrowByteArrayPayloadsAndRetainExactValues(
        ParquetDataPageVersion pageVersion)
    {
        const int valueLength = 4_099;
        var value = Enumerable.Range(0, valueLength)
            .Select(static index => (byte)(index * 17))
            .ToArray();
        var values = Enumerable.Repeat<ReadOnlyMemory<byte>?>(value, 257).ToArray();
        values[7] = null;
        values[19] = ReadOnlyMemory<byte>.Empty;

        var file = CreateUtf8File(values, pageVersion);
        AssertBorrowedUtf8Column(file, values, BorrowedUtf8RowSchema.Projection.Plain,
            EncodingKind.Plain, static row => row.Plain);
        AssertBorrowedUtf8Column(file, values, BorrowedUtf8RowSchema.Projection.Dictionary,
            EncodingKind.RleDictionary, static row => row.Dictionary);
        AssertBorrowedUtf8Column(file, values, BorrowedUtf8RowSchema.Projection.DeltaLength,
            EncodingKind.DeltaLengthByteArray, static row => row.DeltaLength);
        AssertBorrowedUtf8Column(file, values, BorrowedUtf8RowSchema.Projection.DeltaByte,
            EncodingKind.DeltaByteArray, static row => row.DeltaByte);
    }

    delegate RowReaderBinaryValue GetUtf8Value(BorrowedUtf8RowSchema.ReadRow row);

    static void AssertBorrowedUtf8Column(byte[] file, ReadOnlyMemory<byte>?[] expected,
        BorrowedUtf8RowSchema.Projection projection, EncodingKind encoding, GetUtf8Value getValue)
    {
        var pool = new TrackingBufferPool();
        ParquetBuffer retained = default;
        try
        {
            using (var source = new MemoryReadSource(file))
            using (var reader = BorrowedUtf8RowSchema.CreateRowReader(source, projection,
                       new RowReaderOptions { BufferPool = pool }))
            {
                pool.Reset();
                var index = 0;
                while (reader.MoveNext())
                {
                    var row = reader.Current;
                    var expectedValue = expected[index];
                    var actualValue = getValue(row);
                    if (actualValue.IsNull != !expectedValue.HasValue)
                        throw new InvalidOperationException(
                            $"{encoding} returned the wrong null state at row {index}.");
                    if (expectedValue.HasValue &&
                        !actualValue.Span.SequenceEqual(expectedValue.Value.Span))
                        throw new InvalidOperationException(
                            $"{encoding} returned the wrong bytes at row {index}.");

                    if (index == 0)
                    {
                        if (encoding != EncodingKind.DeltaByteArray &&
                            pool.LargestRent >= expectedValue!.Value.Length)
                            throw new InvalidOperationException(
                                $"{encoding} rented a {pool.LargestRent}-byte decoded payload copy.");
                        retained = actualValue.Retain();
                    }
                    index++;
                }
                if (index != expected.Length)
                    throw new InvalidOperationException(
                        $"{encoding} returned {index} rows instead of {expected.Length}.");
            }

            if (!retained.Span.SequenceEqual(expected[0]!.Value.Span))
                throw new InvalidOperationException(
                    $"The retained {encoding} value changed after disposing the reader.");
        }
        finally
        {
            retained.Dispose();
        }
    }

    static byte[] CreateUtf8File(ReadOnlyMemory<byte>?[] values,
        ParquetDataPageVersion pageVersion)
    {
        using var stream = new MemoryStream();
        using var writer = BorrowedUtf8RowSchema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion,
            TargetDataPageSizeBytes = 1024 * 1024,
            WritePageIndexes = false
        });
        var rowGroup = writer.StartRowGroup();
        rowGroup.Plain.Serialize(values);
        rowGroup.Write(rowGroup.Plain);
        rowGroup.Dictionary.Serialize(values);
        rowGroup.Write(rowGroup.Dictionary);
        rowGroup.DeltaLength.Serialize(values);
        rowGroup.Write(rowGroup.DeltaLength);
        rowGroup.DeltaByte.Serialize(values);
        rowGroup.Write(rowGroup.DeltaByte);
        writer.CloseFile();
        return stream.ToArray();
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredPlainInt32LogicalBuffersBorrowPageStorageUntilRetained(
        ParquetDataPageVersion pageVersion)
    {
        var (schema, file, expected) = CreateFile(valueCount: 70_003,
            targetPageSize: 1024 * 1024, pageVersion: pageVersion);
        AssertRequiredPlainLogicalBuffersBorrowPageStorageUntilRetained(schema, file, expected);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredPlainInt64LogicalBuffersBorrowPageStorageUntilRetained(
        ParquetDataPageVersion pageVersion)
    {
        var expected = Enumerable.Range(0, 70_003)
            .Select(static value => (long)value * 1_000_003 - 17)
            .ToArray();
        var (schema, file) = CreatePrimitiveFile(ParquetPhysicalType.Int64, expected, pageVersion);
        AssertRequiredPlainLogicalBuffersBorrowPageStorageUntilRetained(schema, file, expected);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredPlainFloatLogicalBuffersBorrowPageStorageUntilRetained(
        ParquetDataPageVersion pageVersion)
    {
        var expected = Enumerable.Range(0, 70_003)
            .Select(static value => value % 19 == 0 ? float.NaN : value * 0.25f - 17f)
            .ToArray();
        var (schema, file) = CreatePrimitiveFile(ParquetPhysicalType.Float, expected, pageVersion);
        AssertRequiredPlainLogicalBuffersBorrowPageStorageUntilRetained(schema, file, expected);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredPlainDoubleLogicalBuffersBorrowPageStorageUntilRetained(
        ParquetDataPageVersion pageVersion)
    {
        var expected = Enumerable.Range(0, 70_003)
            .Select(static value => value % 19 switch
            {
                0 => BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000042UL)),
                1 => double.NegativeZero,
                _ => value * 0.25 - 17
            })
            .ToArray();
        var (schema, file) = CreatePrimitiveFile(ParquetPhysicalType.Double, expected, pageVersion);
        AssertRequiredPlainLogicalBuffersBorrowPageStorageUntilRetained(schema, file, expected);
    }

    static void AssertRequiredPlainLogicalBuffersBorrowPageStorageUntilRetained<T>(
        ParquetSchema schema, byte[] file, T[] expected) where T : unmanaged
    {
        var pool = new TrackingBufferPool();
        ParquetBuffer retained = default;
        T[] firstValues;
        try
        {
            using (var reader = schema.CreateReader(new MemoryReadSource(file),
                       new ParquetReaderOptions { BufferPool = pool }))
            {
                pool.Reset();
                var buffers = reader.RowGroups[0].Column<T>(0).GetEnumerator();
                try
                {
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException($"Expected a required plain {typeof(T)} buffer.");
                    var first = buffers.Current;
                    firstValues = first.Values.ToArray();
                    if (!ValuesEqual(first.Values, expected.AsSpan(0, first.Count)))
                        throw new InvalidOperationException("The first borrowed buffer contains different values.");
                    if (first.Count >= expected.Length)
                        throw new InvalidOperationException("The borrowed page was not split into bounded buffers.");
                    if (pool.RentCount != 0)
                        throw new InvalidOperationException(
                            $"Borrowed logical values unexpectedly rented {pool.RentCount} buffers.");

                    retained = first.Retain();
                    if (pool.RentCount != 1)
                        throw new InvalidOperationException(
                            $"Retaining borrowed values rented {pool.RentCount} buffers instead of one.");

                    var offset = first.Count;
                    while (buffers.MoveNext())
                    {
                        var current = buffers.Current;
                        if (!ValuesEqual(current.Values, expected.AsSpan(offset, current.Count)))
                            throw new InvalidOperationException(
                                $"A borrowed buffer contains different values at offset {offset}.");
                        offset += current.Count;
                    }
                    if (offset != expected.Length)
                        throw new InvalidOperationException(
                            $"Decoded {offset} values instead of {expected.Length}.");
                    if (pool.RentCount != 1)
                        throw new InvalidOperationException(
                            "Advancing borrowed buffers unexpectedly rented additional storage.");
                }
                finally
                {
                    buffers.Dispose();
                }
            }

            if (!ValuesEqual(retained.AsSpan<T>(), firstValues))
                throw new InvalidOperationException(
                    "Retained borrowed values changed after advancing and disposing the reader.");
        }
        finally
        {
            retained.Dispose();
        }
    }

    static bool ValuesEqual<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right) where T : unmanaged
        => MemoryMarshal.AsBytes(left).SequenceEqual(MemoryMarshal.AsBytes(right));

    [Test]
    public void StreamSourceKeepsOwnedPageBuffer()
    {
        var (_, file, _) = CreateFile();
        var pool = new TrackingBufferPool();
        using var input = new MemoryStream(file, writable: false);
        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { BufferPool = pool });
        reader.Reset(input);
        pool.Reset();

        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");
        if (pool.RentCount == 0)
            throw new InvalidOperationException("Stream-backed page did not rent owned storage.");
    }

    [Test]
    public void NonArrayMemoryKeepsOwnedPageAfterMemoryManagerDisposal()
    {
        var (_, file, _) = CreateFile();
        using var manager = new NonArrayMemoryManager(file);
        var pool = new TrackingBufferPool();
        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { BufferPool = pool });
        reader.Reset(new MemoryReadSource(manager.Memory));
        pool.Reset();
        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");
        var expected = pages.CurrentPayload.ToArray();

        ((IDisposable)manager).Dispose();

        if (!pages.CurrentPayload.SequenceEqual(expected))
            throw new InvalidOperationException("Owned page changed after its source memory manager was disposed.");
        if (pool.RentCount == 0)
            throw new InvalidOperationException("Non-array memory unexpectedly used a borrowed page.");
    }

    [Test]
    public void ReaderResetInvalidatesBorrowedPayload()
    {
        var (_, file, _) = CreateFile();
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(file));
        var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");

        reader.Reset(new MemoryReadSource(file));

        Assert.Throws<InvalidOperationException>(() => ReadPayloadLength(pages));
        pages.Dispose();
    }

    [Test]
    public void ReaderDisposalInvalidatesBorrowedPayload()
    {
        var (_, file, _) = CreateFile();
        var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(file));
        var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");

        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ReadPayloadLength(pages));
        pages.Dispose();
    }

    [Test]
    public void CursorDisposalInvalidatesBorrowedPayload()
    {
        var (_, file, _) = CreateFile();
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(file));
        var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");

        pages.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ReadPayloadLength(pages));
    }

    [Test]
    public async Task BorrowedPageCrcRejectsCorruptPayload()
    {
        var (_, file, _) = CreateFile(writePageCrc: true);
        using (var metadataReader = new ParquetFileReader())
        {
            metadataReader.Reset(new MemoryReadSource(file));
            var pageOffset = metadataReader.Metadata.ColumnChunk(0, 0).DataPageOffset;
            var header = PageHeaderReader.Read(file.AsSpan(checked((int)pageOffset)));
            var payloadOffset = checked((int)pageOffset + header.HeaderLength);
            file[payloadOffset + checked((int)header.CompressedPageSize) / 2] ^= 1;
        }

        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { VerifyPageCrc = true });
        reader.Reset(new MemoryReadSource(file));
        using var pages = reader.OpenPages(0, 0);
        var exception = Assert.Throws<CorruptParquetException>(() => pages.MoveNext());

        await Assert.That(exception.Message).Contains("Page CRC mismatch");
    }

    static int ReadPayloadLength(ParquetPageCursor pages)
        => pages.CurrentPayload.Length;

    static (ParquetSchema Schema, byte[] File, int[] Values) CreateFile(int valueCount = 1_024,
        uint targetPageSize = 1024 * 1024, bool writePageCrc = false,
        ParquetDataPageVersion pageVersion = ParquetDataPageVersion.V1)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var values = Enumerable.Range(0, valueCount).Select(static value => value * 17).ToArray();
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion,
            TargetDataPageSizeBytes = targetPageSize,
            WritePageIndexes = false,
            WritePageCrc = writePageCrc
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return (schema, stream.ToArray(), values);
    }

    static (ParquetSchema Schema, byte[] File) CreatePrimitiveFile<T>(ParquetPhysicalType physicalType,
        T[] values, ParquetDataPageVersion pageVersion) where T : unmanaged
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", physicalType,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion,
            TargetDataPageSizeBytes = 1024 * 1024,
            WritePageIndexes = false
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return (schema, stream.ToArray());
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

    sealed class NonArrayMemoryManager(byte[] bytes) : MemoryManager<byte>
    {
        byte[]? _bytes = bytes;

        public override Span<byte> GetSpan()
            => (_bytes ?? throw new ObjectDisposedException(nameof(NonArrayMemoryManager))).AsSpan();

        public override MemoryHandle Pin(int elementIndex = 0)
            => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
            => _bytes = null;
    }
}
