using System.Collections.Immutable;
using System.Text;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class MergeFileTests
{
    [Test]
    public async Task MergesCompressedRowGroupsWithoutReencoding()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32);
        using var first = WriteFile(schema, [1, 2, 3], new ParquetWriterOptions
        {
            Compression = CompressionKind.Gzip,
            CreatedBy = "first-writer",
            KeyValueMetadata = [new ParquetKeyValueMetadata("source", "first")]
        });
        using var second = WriteFile(schema, [4, 5], new ParquetWriterOptions
        {
            Compression = CompressionKind.Snappy
        });
        var firstChunk = ReadChunkBytes(first, 0);
        var secondChunk = ReadChunkBytes(second, 0);
        first.Position = 2;
        second.Position = 3;

        using var destination = new MemoryStream();
        var merger = schema.CreateMerger(destination, new ParquetMergeOptions
        {
            WriterOptions = new ParquetWriterOptions
            {
                KeyValueMetadata = [new ParquetKeyValueMetadata("merged", "yes")]
            }
        });
        merger.AppendFile(first);
        merger.AppendFile(second);
        await Assert.That(merger.SourceFileCount).IsEqualTo(2);
        await Assert.That(merger.RowGroupCount).IsEqualTo(2);
        await Assert.That(merger.RowCount).IsEqualTo(5L);
        await Assert.That(first.Position).IsEqualTo(2L);
        await Assert.That(second.Position).IsEqualTo(3L);
        merger.CloseFile();

        using var merged = new MemoryStream(destination.ToArray(), writable: false);
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(merged);
        var metadata = physicalReader.Metadata;
        await Assert.That(metadata.RowGroupCount).IsEqualTo(2);
        await Assert.That(metadata.ColumnChunk(0, 0).Compression).IsEqualTo(CompressionKind.Gzip);
        await Assert.That(metadata.ColumnChunk(1, 0).Compression).IsEqualTo(CompressionKind.Snappy);
        await Assert.That(ReadChunkBytes(merged, 0)).IsEquivalentTo(firstChunk);
        await Assert.That(ReadChunkBytes(merged, 1)).IsEquivalentTo(secondChunk);
        await Assert.That(Encoding.UTF8.GetString(metadata.CreatedByUtf8)).IsEqualTo("first-writer");
        await Assert.That(metadata.KeyValueMetadataCount).IsEqualTo(2);
        await Assert.That(Encoding.UTF8.GetString(metadata.KeyValueMetadataKeyUtf8(1))).IsEqualTo("merged");
        await Assert.That(ReadValues(merged, schema)).IsEquivalentTo([1, 2, 3, 4, 5]);

        using var logicalReader = schema.CreateReader(merged);
        using var pages = logicalReader.RowGroups[0].GetColumnMetadata(0).OpenPages();
        await Assert.That(pages.Count).IsGreaterThan(0);
        await Assert.That(pages[0].Offset).IsEqualTo(metadata.ColumnChunk(0, 0).DataPageOffset);
    }

    [Test]
    public async Task MergedFileIsReadableByParquetSharp()
    {
        var path = NewPath();
        try
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32);
            using var first = WriteFile(schema, [10, 20], ParquetWriterOptions.Default);
            using var second = WriteFile(schema, [30], ParquetWriterOptions.Default);
            using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var merger = schema.CreateMerger(destination);
                merger.AppendFile(first);
                merger.AppendFile(second);
                merger.CloseFile();
            }

            using var reader = new ParquetSharp.ParquetFileReader(path);
            await Assert.That(reader.FileMetaData.NumRowGroups).IsEqualTo(2);
            await Assert.That(reader.FileMetaData.NumRows).IsEqualTo(3L);
            using var firstGroup = reader.RowGroup(0);
            using var firstColumn = firstGroup.Column(0).LogicalReader<int>();
            await Assert.That(firstColumn.ReadAll(2)).IsEquivalentTo([10, 20]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task SchemaMismatchLeavesMergerReusable()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32);
        var mismatchedSchema = CreateSchema(ParquetPhysicalType.Int32, "Other");
        using var mismatched = WriteFile(mismatchedSchema, [1, 2], ParquetWriterOptions.Default);
        using var valid = WriteFile(schema, [3, 4], ParquetWriterOptions.Default);
        using var destination = new MemoryStream();
        var merger = schema.CreateMerger(destination);
        var before = destination.ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => merger.AppendFile(mismatched)).ConfigureAwait(false));
        await Assert.That(destination.ToArray()).IsEquivalentTo(before);
        await Assert.That(merger.SourceFileCount).IsEqualTo(0);

        merger.AppendFile(valid);
        merger.CloseFile();
        using var merged = new MemoryStream(destination.ToArray(), writable: false);
        await Assert.That(ReadValues(merged, schema)).IsEquivalentTo([3, 4]);
    }

    static MemoryStream WriteFile(ParquetSchema schema, int[] values, ParquetWriterOptions options)
    {
        using var destination = new MemoryStream();
        var writer = schema.CreateWriter(destination, options);
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return new MemoryStream(destination.ToArray(), writable: false);
    }

    static byte[] ReadChunkBytes(MemoryStream stream, int rowGroupOrdinal)
    {
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var chunk = reader.Metadata.ColumnChunk(rowGroupOrdinal, 0);
        var bytes = new byte[checked((int)chunk.TotalCompressedSize)];
        var position = stream.Position;
        stream.Position = checked((long)chunk.ChunkOffset);
        stream.ReadExactly(bytes);
        stream.Position = position;
        return bytes;
    }

    static int[] ReadValues(MemoryStream stream, ParquetSchema schema)
    {
        var position = stream.Position;
        using var reader = schema.CreateReader(stream);
        var values = new List<int>();
        foreach (var rowGroup in reader.RowGroups)
            foreach (var buffer in rowGroup.Column<int>(0))
                values.AddRange(buffer.Values);
        stream.Position = position;
        return values.ToArray();
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType, string name = "Value")
        => new([
            ColumnDefinition.Leaf(name, physicalType,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);

    static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"plank-merge-{Guid.NewGuid():N}.parquet");
}
