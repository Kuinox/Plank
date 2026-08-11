using System.Buffers;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

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
        uint targetPageSize = 1024 * 1024, bool writePageCrc = false)
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
            DataPageVersion = ParquetDataPageVersion.V1,
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

    sealed class TrackingBufferPool : IParquetBufferPool
    {
        internal int RentCount { get; private set; }

        public ParquetBuffer Rent(uint minimumByteLength)
        {
            RentCount++;
            return DefaultParquetBufferPool.Shared.Rent(minimumByteLength);
        }

        internal void Reset()
            => RentCount = 0;
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
