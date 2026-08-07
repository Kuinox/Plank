using System.Collections.Immutable;
using System.Text;
using Plank.Reading;
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
        var first = WriteFile(schema, [1, 2, 3], new ParquetWriterOptions
        {
            Compression = CompressionKind.Gzip,
            CreatedBy = "first-writer",
            KeyValueMetadata = [new ParquetKeyValueMetadata("source", "first")]
        });
        var second = WriteFile(schema, [4, 5], new ParquetWriterOptions
        {
            Compression = CompressionKind.Snappy
        });
        var firstChunk = ReadChunkBytes(first, 0);
        var secondChunk = ReadChunkBytes(second, 0);

        using var destination = new MemoryParquetSource();
        var merger = schema.CreateMerger(destination, new ParquetMergeOptions
        {
            WriterOptions = new ParquetWriterOptions
            {
                KeyValueMetadata = [new ParquetKeyValueMetadata("merged", "yes")]
            }
        });
        merger.AppendFile(new MemoryReadSource(first));
        merger.AppendFile(new MemoryReadSource(second));
        await Assert.That(merger.SourceFileCount).IsEqualTo(2);
        await Assert.That(merger.RowGroupCount).IsEqualTo(2);
        await Assert.That(merger.RowCount).IsEqualTo(5L);
        merger.CloseFile();

        var mergedBytes = destination.ToArray();
        using var merged = new MemoryStream(mergedBytes, writable: false);
        using var physicalReader = new ParquetFileReader();
        physicalReader.Reset(merged);
        var metadata = physicalReader.Metadata;
        await Assert.That(metadata.RowGroupCount).IsEqualTo(2);
        await Assert.That(metadata.ColumnChunk(0, 0).Compression).IsEqualTo(CompressionKind.Gzip);
        await Assert.That(metadata.ColumnChunk(1, 0).Compression).IsEqualTo(CompressionKind.Snappy);
        await Assert.That(ReadChunkBytes(mergedBytes, 0)).IsEquivalentTo(firstChunk);
        await Assert.That(ReadChunkBytes(mergedBytes, 1)).IsEquivalentTo(secondChunk);
        await Assert.That(Encoding.UTF8.GetString(metadata.CreatedByUtf8)).IsEqualTo("first-writer");
        await Assert.That(metadata.KeyValueMetadataCount).IsEqualTo(2);
        await Assert.That(Encoding.UTF8.GetString(metadata.KeyValueMetadataKeyUtf8(1))).IsEqualTo("merged");
        await Assert.That(ReadValues(mergedBytes, schema)).IsEquivalentTo([1, 2, 3, 4, 5]);

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
            var first = WriteFile(schema, [10, 20], ParquetWriterOptions.Default);
            var second = WriteFile(schema, [30], ParquetWriterOptions.Default);
            using var destination = new MemoryParquetSource();
            var merger = schema.CreateMerger(destination);
            merger.AppendFile(new MemoryReadSource(first));
            merger.AppendFile(new MemoryReadSource(second));
            merger.CloseFile();
            File.WriteAllBytes(path, destination.ToArray());

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
    public async Task MergeInPlaceAppendsSourceFile()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32);
        var existing = WriteFile(schema, [1, 2], new ParquetWriterOptions
        {
            CreatedBy = "existing-writer"
        });
        var source = WriteFile(schema, [3, 4], ParquetWriterOptions.Default);
        using var destination = new MemoryParquetSource(existing);

        var merger = schema.CreateInPlaceMerger(destination);
        merger.AppendFile(new MemoryReadSource(source));
        merger.CloseFile();

        var mergedBytes = destination.ToArray();
        using var merged = new MemoryStream(mergedBytes, writable: false);
        using var reader = new ParquetFileReader();
        reader.Reset(merged);
        await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(2);
        await Assert.That(Encoding.UTF8.GetString(reader.Metadata.CreatedByUtf8)).IsEqualTo("existing-writer");
        await Assert.That(ReadValues(mergedBytes, schema)).IsEquivalentTo([1, 2, 3, 4]);
    }

    [Test]
    public async Task SchemaMismatchLeavesMergerReusable()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32);
        var mismatchedSchema = CreateSchema(ParquetPhysicalType.Int32, "Other");
        var mismatched = WriteFile(mismatchedSchema, [1, 2], ParquetWriterOptions.Default);
        var existing = WriteFile(schema, [3, 4], ParquetWriterOptions.Default);
        var valid = WriteFile(schema, [5, 6], ParquetWriterOptions.Default);
        using var destination = new MemoryParquetSource(existing);
        var merger = schema.CreateInPlaceMerger(destination);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => merger.AppendFile(new MemoryReadSource(mismatched)))
                .ConfigureAwait(false));
        await Assert.That(merger.SourceFileCount).IsEqualTo(0);

        merger.AppendFile(new MemoryReadSource(valid));
        merger.CloseFile();
        await Assert.That(ReadValues(destination.ToArray(), schema)).IsEquivalentTo([3, 4, 5, 6]);
    }

    static byte[] WriteFile(ParquetSchema schema, int[] values, ParquetWriterOptions options)
    {
        using var destination = new MemoryStream();
        var writer = schema.CreateWriter(destination, options);
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return destination.ToArray();
    }

    static byte[] ReadChunkBytes(byte[] bytes, int rowGroupOrdinal)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var chunk = reader.Metadata.ColumnChunk(rowGroupOrdinal, 0);
        var chunkBytes = new byte[checked((int)chunk.TotalCompressedSize)];
        stream.Position = checked((long)chunk.ChunkOffset);
        stream.ReadExactly(chunkBytes);
        return chunkBytes;
    }

    static int[] ReadValues(byte[] bytes, ParquetSchema schema)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = schema.CreateReader(stream);
        var values = new List<int>();
        foreach (var rowGroup in reader.RowGroups)
            foreach (var buffer in rowGroup.Column<int>(0))
                values.AddRange(buffer.Values);
        return values.ToArray();
    }

    sealed class MemoryParquetSource : IParquetReadWriteSource, IDisposable
    {
        readonly MemoryStream _stream;

        internal MemoryParquetSource(byte[]? bytes = null)
        {
            _stream = bytes is null ? new MemoryStream() : new MemoryStream(bytes.Length * 2);
            if (bytes is not null)
                _stream.Write(bytes);
        }

        public ulong Length
            => checked((ulong)_stream.Length);

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
            => throw new NotSupportedException();

        public void Close()
        {
        }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            _stream.Position = checked((long)offset);
            _stream.ReadExactly(destination);
        }

        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            _stream.Position = checked((long)offset);
            _stream.Write(source);
        }

        public void SetLength(ulong length)
            => _stream.SetLength(checked((long)length));

        public void Flush()
            => _stream.Flush();

        public void Dispose()
            => _stream.Dispose();

        internal byte[] ToArray()
            => _stream.ToArray();
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType, string name = "Value")
        => new([
            ColumnDefinition.Leaf(name, physicalType,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);

    static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"plank-merge-{Guid.NewGuid():N}.parquet");
}
