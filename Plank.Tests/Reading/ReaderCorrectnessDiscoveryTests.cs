using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using ZstdSharp;

namespace Plank.Tests.Reading;

internal sealed class ReaderCorrectnessDiscoveryTests
{
    [Test]
    public async Task RequiredPageMayFallBackToPlainAfterDictionaryPage()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)
        ]);
        var column = schema.LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<int>);

        try
        {
            var dictionaryPayload = new byte[sizeof(int) * 2];
            BinaryPrimitives.WriteInt32LittleEndian(dictionaryPayload, 10);
            BinaryPrimitives.WriteInt32LittleEndian(dictionaryPayload.AsSpan(sizeof(int)), 20);
            var dictionaryHeader = CreatePageHeader(PageHeaderType.DictionaryPage, valueCount: 2,
                EncodingKind.Plain, dictionaryPayload.Length);

            var dictionaryDecoded = ColumnChunkReader.TryDecodeDictionaryPageIntoNative(
                dictionaryHeader, dictionaryPayload, column, ref buffers, DefaultParquetBufferPool.Shared);

            await Assert.That(dictionaryDecoded).IsTrue();

            var plainPayload = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(plainPayload, 30);
            var plainHeader = CreatePageHeader(PageHeaderType.DataPage, valueCount: 1,
                EncodingKind.Plain, plainPayload.Length);

            var plainDecoded = ColumnChunkReader.TryDecodeRequiredPageIntoNative(
                plainHeader, plainPayload, column, rowCount: 1, ref buffers, DefaultParquetBufferPool.Shared,
                out var values);

            await Assert.That(plainDecoded).IsTrue();
            await Assert.That(values.Values.ToArray()).IsEquivalentTo([30]);
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(4)]
    public void BooleanRleRejectsMismatchedEncodedLength(int encodedLength)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Boolean)
        ]);
        var column = schema.LeafColumns[0].Column;
        byte[] payload = new byte[sizeof(int) + 3];
        BinaryPrimitives.WriteInt32LittleEndian(payload, encodedLength);
        payload[sizeof(int)] = 0x10;
        var header = CreatePageHeader(PageHeaderType.DataPage, valueCount: 8,
            EncodingKind.Rle, payload.Length);
        var buffers = default(ColumnReadBuffers<bool>);

        try
        {
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryDecodeRequiredPageIntoNative(
                    header, payload, column, rowCount: 8, ref buffers,
                    DefaultParquetBufferPool.Shared, out _));
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void SnappyRejectsOutputShorterThanPageHeaderSize()
    {
        byte[] compressed = [0x04, 0x0C, 1, 2, 3, 4];

        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.Decompress(compressed, expectedLength: 5, CompressionKind.Snappy));
    }

    [Test]
    public void Lz4RejectsOutputShorterThanPageHeaderSize()
    {
        byte[] compressed = [0x40, 1, 2, 3, 4];

        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.Decompress(compressed, expectedLength: 5, CompressionKind.Lz4));
    }

    [Test]
    public void ZstdRejectsOutputShorterThanPageHeaderSize()
    {
        byte[] input = [1, 2, 3, 4];
        using var compressor = new Compressor(1);
        var compressed = new byte[Compressor.GetCompressBound(input.Length)];
        var compressedLength = compressor.Wrap(input, compressed, 0);

        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.Decompress(compressed.AsSpan(0, compressedLength),
                expectedLength: 5, CompressionKind.Zstd));
    }

    [Test]
    public void PageCursorRejectsCompressedPageWithMissingPayload()
    {
        var file = CreateSingleValueFile(CompressionKind.Snappy);
        int pageOffset;
        using (var source = new MemoryStream(file, writable: false))
        using (var reader = new ParquetFileReader())
        {
            reader.Reset(source);
            pageOffset = checked((int)reader.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }

        var header = PageHeaderReader.Read(file.AsSpan(pageOffset));
        if (!header.IsCompressed || header.UncompressedPageSize == 0 || header.CompressedPageSize > 63)
            throw new InvalidOperationException("The compressed-page test fixture has an unexpected page header.");

        var compressedSizeOffset = FindThirdCompactI32ValueOffset(file.AsSpan(pageOffset));
        file[pageOffset + compressedSizeOffset] = 0;

        Assert.Throws<CorruptParquetException>(() =>
        {
            using var source = new MemoryStream(file, writable: false);
            using var reader = new ParquetFileReader();
            reader.Reset(source);
            using var cursor = reader.OpenPages(0, 0);
            _ = cursor.MoveNext();
        });
    }

    [Test]
    public async Task LegacyTimeMillisIsAdjustedToUtc()
    {
        var logicalType = ReadLegacyLogicalType(physicalType: 1, convertedType: 7);

        await Assert.That(logicalType.Kind).IsEqualTo(LogicalTypeKind.Time);
        await Assert.That(logicalType.Unit).IsEqualTo(TimeUnit.Millis);
        await Assert.That(logicalType.IsAdjustedToUtc).IsTrue();
    }

    [Test]
    public async Task LegacyTimestampMillisIsAdjustedToUtc()
    {
        var logicalType = ReadLegacyLogicalType(physicalType: 2, convertedType: 9);

        await Assert.That(logicalType.Kind).IsEqualTo(LogicalTypeKind.Timestamp);
        await Assert.That(logicalType.Unit).IsEqualTo(TimeUnit.Millis);
        await Assert.That(logicalType.IsAdjustedToUtc).IsTrue();
    }

    static PageHeader CreatePageHeader(PageHeaderType type, uint valueCount, EncodingKind encoding,
        int payloadLength)
        => new(type, checked((uint)payloadLength), checked((uint)payloadLength), valueCount, encoding,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 0, NullCount: 0,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: valueCount);

    static LogicalTypeInfo ReadLegacyLogicalType(byte physicalType, byte convertedType)
    {
        byte[] footer =
        [
            0x15, 0x02,
            0x19, 0x2C,
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x02,
            0x00,
            0x15, checked((byte)(physicalType << 1)),
            0x25, 0x00,
            0x18, 0x02, (byte)'t', (byte)'s',
            0x25, checked((byte)(convertedType << 1)),
            0x00,
            0x16, 0x00,
            0x19, 0x0C,
            0x00
        ];

        using var stream = new MemoryStream();
        stream.Write("PAR1"u8);
        stream.Write(footer);
        Span<byte> footerLength = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(footerLength, checked((uint)footer.Length));
        stream.Write(footerLength);
        stream.Write("PAR1"u8);
        stream.Position = 0;

        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        return reader.Metadata.SchemaNodes[1].LogicalType;
    }

    static byte[] CreateSingleValueFile(CompressionKind compression)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        serialized.Serialize([42]);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    static int FindThirdCompactI32ValueOffset(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        for (var fieldId = 1; fieldId <= 3; fieldId++)
        {
            if ((uint)offset >= (uint)bytes.Length)
                throw new InvalidOperationException("The page header ended before its compressed-size field.");

            var fieldHeader = bytes[offset++];
            if ((fieldHeader & 0x0F) != 5 || fieldHeader >> 4 != 1)
                throw new InvalidOperationException("The page header does not start with three compact i32 fields.");

            var valueOffset = offset;
            while (true)
            {
                if ((uint)offset >= (uint)bytes.Length)
                    throw new InvalidOperationException("The page-header varint is truncated.");
                if ((bytes[offset++] & 0x80) == 0)
                    break;
            }

            if (fieldId != 3)
                continue;
            if (offset - valueOffset != 1)
                throw new InvalidOperationException("The compressed-size test fixture needs a one-byte varint.");
            return valueOffset;
        }

        throw new InvalidOperationException("The page header is missing its compressed-size field.");
    }
}
