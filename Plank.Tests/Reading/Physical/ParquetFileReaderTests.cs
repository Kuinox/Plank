using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ParquetFileReaderTests
{
    [Test]
    public async Task ExposesPhysicalSchemaRowGroupsAndColumnChunks()
    {
        using var stream = CreateFile(CompressionKind.None);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var metadata = reader.Metadata;

        await Assert.That(metadata.FileVersion).IsEqualTo(1);
        await Assert.That(metadata.FooterOffset).IsGreaterThan(0UL);
        await Assert.That(metadata.FooterLength).IsGreaterThan(0U);
        await Assert.That(metadata.SchemaNodeCount).IsEqualTo(2);
        await Assert.That(metadata.ColumnCount).IsEqualTo(1);
        await Assert.That(metadata.RowGroupCount).IsEqualTo(1);

        var root = metadata.SchemaNodes[0];
        var leaf = metadata.SchemaNodes[1];
        var column = metadata.ColumnSchema(0);
        var rowGroup = metadata.RowGroup(0);
        var chunk = metadata.ColumnChunk(rowGroup.Ordinal, 0);

        await Assert.That(root.Kind).IsEqualTo(NodeKind.Group);
        await Assert.That(leaf.Kind).IsEqualTo(NodeKind.Leaf);
        await Assert.That(leaf.ParentOrdinal).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(metadata.SchemaNodeNameUtf8(leaf.Ordinal))).IsEqualTo("Value");
        await Assert.That(column.PhysicalType).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.That(column.PathSegmentCount).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(metadata.ColumnPathSegmentUtf8(column.Ordinal, 0))).IsEqualTo("Value");
        await Assert.That(rowGroup.RowCount).IsEqualTo(3UL);
        await Assert.That(rowGroup.ColumnCount).IsEqualTo(1);
        await Assert.That(chunk.TotalCompressedSize).IsGreaterThan(0UL);
        await Assert.That(chunk.Encodings.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task PageCursorExposesUncompressedPayload()
    {
        using var stream = CreateFile(CompressionKind.Snappy);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        using var cursor = reader.OpenPages(0, 0);

        await Assert.That(cursor.MoveNext()).IsTrue();
        await Assert.That(cursor.CurrentHeader.Type).IsEqualTo(PageHeaderType.DataPageV2);
        await Assert.That(cursor.CurrentPayload.Length)
            .IsEqualTo(checked((int)cursor.CurrentHeader.UncompressedPageSize));
        await Assert.That(cursor.MoveNext()).IsFalse();
    }

    [Test]
    public async Task PageCursorSupportsForeach()
    {
        using var stream = CreateFile(CompressionKind.Snappy);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);

        var pageCount = 0;
        var header = default(PageHeader);
        var payloadLength = 0;
        foreach (var page in reader.OpenPages(0, 0))
        {
            pageCount++;
            header = page.Header;
            payloadLength = page.Payload.Length;
        }

        await Assert.That(pageCount).IsEqualTo(1);
        await Assert.That(header.Type).IsEqualTo(PageHeaderType.DataPageV2);
        await Assert.That(payloadLength).IsEqualTo(checked((int)header.UncompressedPageSize));
    }

    [Test]
    public async Task ResetDoesNotInvalidateExistingMetadataValues()
    {
        using var first = CreateFile(CompressionKind.None);
        using var second = CreateTwoColumnTwoRowGroupFile();
        using var reader = new ParquetFileReader();
        reader.Reset(first);
        var metadata = reader.Metadata;
        var column = metadata.ColumnSchema(0);

        reader.Reset(second);

        await Assert.That(column.PhysicalType).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.That(metadata.ColumnCount).IsEqualTo(2);
        await Assert.That(metadata.RowGroupCount).IsEqualTo(2);
    }

    [Test]
    public async Task FailedResetDoesNotInvalidateExistingMetadataValuesAndClearsMetadata()
    {
        using var stream = CreateFile(CompressionKind.None);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var column = reader.Metadata.ColumnSchema(0);

        await Assert.ThrowsAsync<CorruptParquetException>(async () =>
            await Task.Run(() => reader.Reset(new MemoryStream([1, 2, 3]))).ConfigureAwait(false));

        await Assert.That(reader.Metadata.ColumnCount).IsEqualTo(0);
        await Assert.That(reader.Metadata.SchemaNodes.Length).IsEqualTo(0);
        await Assert.That(column.PhysicalType).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => reader.Metadata.ColumnSchema(0)).ConfigureAwait(false));
    }

    [Test]
    public async Task FailedFooterParseClearsPartialMetadata()
    {
        using var reader = new ParquetFileReader();

        await Assert.ThrowsAsync<CorruptParquetException>(async () =>
            await Task.Run(() => reader.Reset(CreatePartialSchemaFooterFile())).ConfigureAwait(false));

        await Assert.That(reader.Metadata.ColumnCount).IsEqualTo(0);
        await Assert.That(reader.Metadata.SchemaNodes.Length).IsEqualTo(0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => reader.Metadata.ColumnSchema(0)).ConfigureAwait(false));
    }

    [Test]
    public async Task ResetSupportsDifferentColumnAndRowGroupCounts()
    {
        using var first = CreateFile(CompressionKind.None);
        using var second = CreateTwoColumnTwoRowGroupFile();
        using var reader = new ParquetFileReader();
        reader.Reset(first);

        reader.Reset(second);
        var metadata = reader.Metadata;

        await Assert.That(metadata.ColumnCount).IsEqualTo(2);
        await Assert.That(metadata.RowGroupCount).IsEqualTo(2);
        await Assert.That(metadata.RowGroup(1).ColumnCount).IsEqualTo(2);
    }

    [Test]
    public async Task InvalidOrdinalsAndDisposedAccessThrow()
    {
        using var stream = CreateFile(CompressionKind.None);
        var reader = new ParquetFileReader();
        reader.Reset(stream);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => reader.Metadata.ColumnSchema(1)).ConfigureAwait(false));

        var rowGroup = reader.Metadata.RowGroup(0);
        reader.Dispose();

        await Assert.That(rowGroup.RowCount).IsEqualTo(3UL);
    }

    static MemoryStream CreateFile(CompressionKind compression)
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        serialized.Serialize([1, 2, 3]);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return new MemoryStream(stream.ToArray());
    }

    static MemoryStream CreateTwoColumnTwoRowGroupFile()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32),
            new Column("Other", ParquetPhysicalType.Int64)
        ]);
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        for (var i = 0; i < 2; i++)
        {
            var rowGroup = writer.StartRowGroup();
            var value = rowGroup.CreateSerializedColumn<int>(schema.Columns[0]);
            value.Serialize([i]);
            rowGroup.Write(value);
            var other = rowGroup.CreateSerializedColumn<long>(schema.Columns[1]);
            other.Serialize([i]);
            rowGroup.Write(other);
        }
        writer.CloseFile();
        return new MemoryStream(stream.ToArray());
    }

    static MemoryStream CreatePartialSchemaFooterFile()
    {
        byte[] footer =
        [
            0x15, 0x02,
            0x19, 0x2C,
            0x15, 0x02,
            0x25, 0x00,
            0x18, 0x01, (byte)'x',
            0x00
        ];

        var stream = new MemoryStream();
        stream.Write("PAR1"u8);
        stream.Write(footer);
        Span<byte> footerLength = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(footerLength, checked((uint)footer.Length));
        stream.Write(footerLength);
        stream.Write("PAR1"u8);
        stream.Position = 0;
        return stream;
    }

}
