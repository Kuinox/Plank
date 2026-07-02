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

        await Assert.That(reader.FileVersion).IsEqualTo(1);
        await Assert.That(reader.FooterOffset).IsGreaterThan(0UL);
        await Assert.That(reader.FooterLength).IsGreaterThan(0U);
        await Assert.That(reader.SchemaNodeCount).IsEqualTo(2);
        await Assert.That(reader.ColumnCount).IsEqualTo(1);
        await Assert.That(reader.RowGroupCount).IsEqualTo(1);

        var root = reader.SchemaNode(0);
        var leaf = reader.SchemaNode(1);
        var column = reader.ColumnSchema(0);
        var rowGroup = reader.RowGroup(0);
        var chunk = rowGroup.ColumnChunk(0);

        await Assert.That(root.Kind).IsEqualTo(NodeKind.Group);
        await Assert.That(leaf.Kind).IsEqualTo(NodeKind.Leaf);
        await Assert.That(leaf.ParentOrdinal).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(leaf.NameUtf8)).IsEqualTo("Value");
        await Assert.That(column.PhysicalType).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.That(column.PathSegmentCount).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(column.PathSegmentUtf8(0))).IsEqualTo("Value");
        await Assert.That(rowGroup.RowCount).IsEqualTo(3UL);
        await Assert.That(rowGroup.ColumnCount).IsEqualTo(1);
        await Assert.That(chunk.TotalCompressedSize).IsGreaterThan(0UL);
        await Assert.That(chunk.Encodings.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task PageCursorExposesCompressedAndUncompressedPayloads()
    {
        using var stream = CreateFile(CompressionKind.Snappy);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        using var cursor = reader.RowGroup(0).OpenPages(0);

        await Assert.That(cursor.MoveNext()).IsTrue();
        await Assert.That(cursor.CurrentHeader.Type).IsEqualTo(PageHeaderType.DataPageV2);
        await Assert.That(cursor.CurrentCompressedPayload.Length).IsGreaterThan(0);
        await Assert.That(cursor.CurrentPayload.Length)
            .IsEqualTo(checked((int)cursor.CurrentHeader.UncompressedPageSize));
        await Assert.That(cursor.MoveNext()).IsFalse();
    }

    [Test]
    public async Task ResetInvalidatesExistingHandles()
    {
        using var first = CreateFile(CompressionKind.None);
        using var second = CreateFile(CompressionKind.None);
        using var reader = new ParquetFileReader();
        reader.Reset(first);
        var column = reader.ColumnSchema(0);

        reader.Reset(second);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => _ = column.PhysicalType).ConfigureAwait(false));
    }

    [Test]
    public async Task ResetSupportsDifferentColumnAndRowGroupCounts()
    {
        using var first = CreateFile(CompressionKind.None);
        using var second = CreateTwoColumnTwoRowGroupFile();
        using var reader = new ParquetFileReader();
        reader.Reset(first);

        reader.Reset(second);

        await Assert.That(reader.ColumnCount).IsEqualTo(2);
        await Assert.That(reader.RowGroupCount).IsEqualTo(2);
        await Assert.That(reader.RowGroup(1).ColumnCount).IsEqualTo(2);
    }

    [Test]
    public async Task InvalidOrdinalsAndDisposedAccessThrow()
    {
        using var stream = CreateFile(CompressionKind.None);
        var reader = new ParquetFileReader();
        reader.Reset(stream);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => reader.ColumnSchema(1)).ConfigureAwait(false));

        var rowGroup = reader.RowGroup(0);
        reader.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await Task.Run(() => _ = rowGroup.RowCount).ConfigureAwait(false));
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
}
