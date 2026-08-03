using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class PageCrcTests
{
    [Test]
    public void Crc32MatchesStandardCheckValue()
    {
        var actual = ParquetCrc32.Compute("123456789"u8);

        if (actual != 0xCBF43926U)
            throw new InvalidOperationException($"Expected CRC32 0xCBF43926, got 0x{actual:X8}.");
    }

    [Test]
    [Arguments(CompressionKind.None)]
    [Arguments(CompressionKind.Gzip)]
    public void WriterChecksumsExactStoredPayloadAndReaderAcceptsIt(CompressionKind compression)
    {
        var file = CreatePlainFile(compression, writePageCrc: true);
        var (pageOffset, header) = ReadDataPageHeader(file);
        if (!header.Crc.HasValue)
            throw new InvalidOperationException("Expected the data page header to contain a CRC.");

        var payload = file.AsSpan(checked((int)pageOffset + header.HeaderLength),
            checked((int)header.CompressedPageSize));
        var actual = ParquetCrc32.Compute(payload);
        if (actual != header.Crc.Value)
            throw new InvalidOperationException(
                $"Expected stored CRC 0x{header.Crc.Value:X8}, computed 0x{actual:X8}.");

        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { VerifyPageCrc = true });
        reader.Reset(new MemoryStream(file, writable: false));
        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");
        if (pages.MoveNext())
            throw new InvalidOperationException("Expected exactly one data page.");
    }

    [Test]
    [Arguments(CompressionKind.None)]
    [Arguments(CompressionKind.Gzip)]
    public async Task ReaderRejectsCorruptStoredPayload(CompressionKind compression)
    {
        var file = CreatePlainFile(compression, writePageCrc: true);
        var (pageOffset, header) = ReadDataPageHeader(file);
        var payloadOffset = checked((int)pageOffset + header.HeaderLength);
        var payloadLength = checked((int)header.CompressedPageSize);
        if (payloadLength == 0)
            throw new InvalidOperationException("Expected a non-empty page payload.");
        file[payloadOffset + payloadLength / 2] ^= 0x01;

        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { VerifyPageCrc = true });
        reader.Reset(new MemoryStream(file, writable: false));
        using var pages = reader.OpenPages(0, 0);
        var exception = Assert.Throws<CorruptParquetException>(() => pages.MoveNext());

        await Assert.That(exception.Message).Contains("Page CRC mismatch");
        await Assert.That(exception.Message).Contains("row group 0, column 0");
    }

    [Test]
    public void WriterChecksumsDictionaryAndDataPages()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        var file = WriteFile(schema, CompressionKind.Gzip, writePageCrc: true, [10, 20, 10, 20]);

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(file, writable: false));
        var chunk = reader.Metadata.ColumnChunk(0, 0);
        AssertStoredPageCrc(file, chunk.DictionaryPageOffset, PageHeaderType.DictionaryPage);
        AssertStoredPageCrc(file, chunk.DataPageOffset, PageHeaderType.DataPageV2);
    }

    [Test]
    public void WriterChecksumsSplitDataPageV2Payload()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.Gzip,
            WritePageCrc = true,
            WritePageIndexes = false
        });
        var serialized = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
        serialized.Serialize([1, null, 3, null, 5]);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        var file = stream.ToArray();
        var (pageOffset, header) = ReadDataPageHeader(file);

        if (!header.IsCompressed || header.DefinitionLevelsByteLength == 0)
            throw new InvalidOperationException("Expected compressed values with uncompressed definition levels.");
        AssertStoredPageCrc(file, pageOffset, PageHeaderType.DataPageV2);

        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { VerifyPageCrc = true });
        reader.Reset(new MemoryStream(file, writable: false));
        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");
    }

    [Test]
    [Arguments(ParquetSharp.Compression.Uncompressed)]
    [Arguments(ParquetSharp.Compression.Gzip)]
    public void ReaderVerifiesParquetSharpDataPageV1(ParquetSharp.Compression compression)
    {
        var file = CreateParquetSharpDataPageV1File(compression);
        var (pageOffset, header) = ReadDataPageHeader(file);
        if (header.Type != PageHeaderType.DataPage)
            throw new InvalidOperationException($"Expected a Data Page V1, got {header.Type}.");
        AssertStoredPageCrc(file, pageOffset, PageHeaderType.DataPage);

        using var reader = new ParquetFileReader(new ParquetFileReaderOptions { VerifyPageCrc = true });
        reader.Reset(new MemoryStream(file, writable: false));
        using var pages = reader.OpenPages(0, 0);
        if (!pages.MoveNext())
            throw new InvalidOperationException("Expected a data page.");
    }

    [Test]
    public void WriterOmitsPageCrcByDefault()
    {
        var file = CreatePlainFile(CompressionKind.None, writePageCrc: false);
        var (_, header) = ReadDataPageHeader(file);

        if (header.Crc.HasValue)
            throw new InvalidOperationException("Page CRC should be absent when writing is not enabled.");
    }

    [Test]
    public async Task LogicalReaderPropagatesPageCrcVerification()
    {
        var schema = CreatePlainSchema();
        var file = WriteFile(schema, CompressionKind.None, writePageCrc: true, CreateValues());
        var (pageOffset, header) = ReadDataPageHeader(file);
        file[checked((int)pageOffset + header.HeaderLength)] ^= 0x01;

        using var reader = schema.CreateReader(new MemoryStream(file, writable: false),
            new ParquetReaderOptions { VerifyPageCrc = true });
        var values = reader.RowGroups[0].Column<int>(schema.LeafColumns[0]).GetEnumerator();
        var exception = Assert.Throws<CorruptParquetException>(() => values.MoveNext());
        values.Dispose();

        await Assert.That(exception.Message).Contains("Page CRC mismatch");
    }

    static byte[] CreatePlainFile(CompressionKind compression, bool writePageCrc)
    {
        var schema = CreatePlainSchema();
        return WriteFile(schema, compression, writePageCrc, CreateValues());
    }

    static byte[] CreateParquetSharpDataPageV1File(ParquetSharp.Compression compression)
    {
        using var stream = new MemoryStream();
        using var propertiesBuilder = new ParquetSharp.WriterPropertiesBuilder();
        using var properties = propertiesBuilder.Compression(compression)
            .DisableDictionary()
            .EnablePageChecksum()
            .Build();
        using var writer = new ParquetSharp.ParquetFileWriter(stream,
            [new ParquetSharp.Column<int>("value")], null, properties, null, true);
        using (var rowGroup = writer.AppendRowGroup())
        using (var column = rowGroup.NextColumn().LogicalWriter<int>())
            column.WriteBatch(CreateValues());
        writer.Close();
        return stream.ToArray();
    }

    static ParquetSchema CreatePlainSchema()
        => new([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);

    static int[] CreateValues()
    {
        var values = new int[4096];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 257;
        return values;
    }

    static byte[] WriteFile(ParquetSchema schema, CompressionKind compression, bool writePageCrc, int[] values)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression,
            WritePageCrc = writePageCrc,
            WritePageIndexes = false
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    static (ulong Offset, PageHeader Header) ReadDataPageHeader(byte[] file)
    {
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(file, writable: false));
        var offset = reader.Metadata.ColumnChunk(0, 0).DataPageOffset;
        return (offset, PageHeaderReader.Read(file.AsSpan(checked((int)offset))));
    }

    static void AssertStoredPageCrc(byte[] file, ulong offset, PageHeaderType expectedType)
    {
        var header = PageHeaderReader.Read(file.AsSpan(checked((int)offset)));
        if (header.Type != expectedType)
            throw new InvalidOperationException($"Expected {expectedType}, got {header.Type}.");
        if (!header.Crc.HasValue)
            throw new InvalidOperationException($"Expected {expectedType} to contain a CRC.");

        var payload = file.AsSpan(checked((int)offset + header.HeaderLength),
            checked((int)header.CompressedPageSize));
        var actual = ParquetCrc32.Compute(payload);
        if (actual != header.Crc.Value)
            throw new InvalidOperationException(
                $"Expected {expectedType} CRC 0x{header.Crc.Value:X8}, computed 0x{actual:X8}.");
    }
}
