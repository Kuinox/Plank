using System.Buffers.Binary;
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
    public async Task PreservesSortingColumnsForEachImportedRowGroup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Id", ParquetPhysicalType.Int32),
            ColumnDefinition.RequiredLeaf("Sequence", ParquetPhysicalType.Int32)
        ]);
        var first = WriteTwoColumnFile(schema, [1, 2], [20, 10], new ParquetWriterOptions
        {
            SortingColumns =
            [
                new ParquetSortingColumn(0),
                new ParquetSortingColumn(1, descending: true, nullsFirst: true)
            ]
        });
        var second = WriteTwoColumnFile(schema, [4, 3], [30, 40], new ParquetWriterOptions
        {
            SortingColumns = [new ParquetSortingColumn(0, descending: true, nullsFirst: false)]
        });

        using var destination = new MemoryParquetSource();
        var merger = schema.CreateMerger(new MemoryReadSource(first), destination);
        merger.AppendFile(new MemoryReadSource(second));
        merger.CloseFile();

        using var stream = new MemoryStream(destination.ToArray(), writable: false);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var firstSorting = reader.Metadata.RowGroupSortingColumns(0).ToArray();
        var secondSorting = reader.Metadata.RowGroupSortingColumns(1).ToArray();
        await Assert.That(firstSorting.Length).IsEqualTo(2);
        await Assert.That(firstSorting[0]).IsEqualTo(new ParquetSortingColumn(0));
        await Assert.That(firstSorting[1]).IsEqualTo(new ParquetSortingColumn(1,
            descending: true, nullsFirst: true));
        await Assert.That(secondSorting.Length).IsEqualTo(1);
        await Assert.That(secondSorting[0]).IsEqualTo(new ParquetSortingColumn(0,
            descending: true, nullsFirst: false));
    }

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
        var merger = schema.CreateMerger(new MemoryReadSource(first), destination, new ParquetMergeOptions
        {
            WriterOptions = new ParquetWriterOptions
            {
                KeyValueMetadata = [new ParquetKeyValueMetadata("merged", "yes")]
            }
        });
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
            var merger = schema.CreateMerger(new MemoryReadSource(first), destination);
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
    public async Task MergesBloomFiltersAndPageIndexes()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(bloomFilter: new ParquetBloomFilterOptions
                {
                    ExpectedDistinctValueCount = 8
                }))
        ]);
        var options = new ParquetWriterOptions
        {
            WritePageIndexes = true,
            TargetDataPageSizeBytes = 16
        };
        var first = RemoveBloomFilterLength(WriteFile(schema, [10, 20], options));
        var second = WriteFile(schema, [30, 40], options);
        var firstBloom = ReadBloomBitset(first, 0);
        var secondBloom = ReadBloomBitset(second, 0);
        var firstSnapshot = first.ToArray();
        var secondSnapshot = second.ToArray();

        using (var source = new ParquetFileReader())
        {
            source.Reset(new MemoryReadSource(first));
            await Assert.That(source.Metadata.ColumnChunk(0, 0).HasBloomFilter).IsTrue();
            await Assert.That(source.Metadata.ColumnChunk(0, 0).BloomFilterLength).IsEqualTo(0U);
        }

        using var destination = new MemoryParquetSource();
        var merger = schema.CreateMerger(new MemoryReadSource(first), destination);
        merger.AppendFile(new MemoryReadSource(second));
        merger.CloseFile();

        await Assert.That(first.AsSpan().SequenceEqual(firstSnapshot)).IsTrue();
        await Assert.That(second.AsSpan().SequenceEqual(secondSnapshot)).IsTrue();
        var mergedBytes = destination.ToArray();
        using var merged = new MemoryStream(mergedBytes, writable: false);
        using var reader = new ParquetFileReader();
        reader.Reset(merged);
        await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(2);
        for (var rowGroupOrdinal = 0; rowGroupOrdinal < 2; rowGroupOrdinal++)
        {
            var chunk = reader.Metadata.ColumnChunk(rowGroupOrdinal, 0);
            await Assert.That(chunk.HasBloomFilter).IsTrue();
            await Assert.That(chunk.BloomFilterLength).IsGreaterThan(0U);
            await Assert.That(chunk.ColumnIndexLength).IsGreaterThan(0U);
            await Assert.That(chunk.OffsetIndexLength).IsGreaterThan(0U);
        }
        await Assert.That(ReadBloomBitset(mergedBytes, 0).AsSpan().SequenceEqual(firstBloom)).IsTrue();
        await Assert.That(ReadBloomBitset(mergedBytes, 1).AsSpan().SequenceEqual(secondBloom)).IsTrue();
        using var firstFilter = reader.OpenBloomFilter(0, 0);
        using var secondFilter = reader.OpenBloomFilter(1, 0);
        await Assert.That(firstFilter.MightContain(10)).IsTrue();
        await Assert.That(secondFilter.MightContain(40)).IsTrue();
        await Assert.That(ReadValues(mergedBytes, schema)).IsEquivalentTo([10, 20, 30, 40]);
        using var logicalReader = schema.CreateReader(new MemoryReadSource(mergedBytes));
        for (var rowGroupOrdinal = 0; rowGroupOrdinal < 2; rowGroupOrdinal++)
        {
            using var pages = logicalReader.RowGroups[rowGroupOrdinal].GetColumnMetadata(0).OpenPages();
            await Assert.That(pages.Count).IsGreaterThan(0);
            await Assert.That(pages[0].Offset)
                .IsEqualTo(reader.Metadata.ColumnChunk(rowGroupOrdinal, 0).DataPageOffset);
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

        var merger = schema.CreateMerger(destination);
        merger.AppendFile(new MemoryReadSource(source));
        await Assert.That(merger.SourceFileCount).IsEqualTo(2);
        await Assert.That(merger.RowGroupCount).IsEqualTo(2);
        await Assert.That(merger.RowCount).IsEqualTo(4L);
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
        var merger = schema.CreateMerger(destination);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => merger.AppendFile(new MemoryReadSource(mismatched)))
                .ConfigureAwait(false));
        await Assert.That(merger.SourceFileCount).IsEqualTo(1);
        await Assert.That(merger.RowGroupCount).IsEqualTo(1);
        await Assert.That(merger.RowCount).IsEqualTo(2L);

        merger.AppendFile(new MemoryReadSource(valid));
        await Assert.That(merger.SourceFileCount).IsEqualTo(2);
        await Assert.That(merger.RowGroupCount).IsEqualTo(2);
        await Assert.That(merger.RowCount).IsEqualTo(4L);
        merger.CloseFile();
        await Assert.That(ReadValues(destination.ToArray(), schema)).IsEquivalentTo([3, 4, 5, 6]);
    }

    [Test]
    public async Task OutOfPlaceMergeRejectsTheSameSourceAndDestination()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32);
        var existing = WriteFile(schema, [1, 2], ParquetWriterOptions.Default);
        using var file = new MemoryParquetSource(existing);

        await Assert.That(() => schema.CreateMerger(file, file)).Throws<ArgumentException>();
        await Assert.That(ReadValues(file.ToArray(), schema)).IsEquivalentTo([1, 2]);
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

    static byte[] WriteTwoColumnFile(ParquetSchema schema, int[] firstValues, int[] secondValues,
        ParquetWriterOptions options)
    {
        using var destination = new MemoryStream();
        var writer = schema.CreateWriter(destination, options);
        var first = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);
        first.Serialize(firstValues);
        second.Serialize(secondValues);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(first);
        rowGroup.Write(second);
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

    static byte[] ReadBloomBitset(byte[] bytes, int rowGroupOrdinal)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        using var bloomFilter = reader.OpenBloomFilter(rowGroupOrdinal, 0);
        return bloomFilter.Bitset.ToArray();
    }

    static byte[] RemoveBloomFilterLength(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        var metadata = reader.Metadata;
        var footer = bytes.AsSpan(checked((int)metadata.FooterOffset), checked((int)metadata.FooterLength)).ToArray();

        for (var rowGroupOrdinal = 0; rowGroupOrdinal < metadata.RowGroupCount; rowGroupOrdinal++)
        {
            var chunk = metadata.ColumnChunk(rowGroupOrdinal, 0);
            var offset = EncodeCompactInteger(checked((long)chunk.BloomFilterOffset));
            var length = EncodeCompactInteger(chunk.BloomFilterLength);
            var pattern = new byte[checked(2 + offset.Length + length.Length)];
            pattern[0] = 0x26; // Field 14, compact I64, two fields after statistics.
            offset.CopyTo(pattern.AsSpan(1));
            pattern[1 + offset.Length] = 0x15; // Field 15, compact I32, one-field delta.
            length.CopyTo(pattern.AsSpan(2 + offset.Length));
            var patternOffset = footer.AsSpan().IndexOf(pattern);
            if (patternOffset < 0)
                throw new InvalidOperationException("The fixture's Bloom-filter metadata fields were not found.");
            var lengthFieldOffset = checked(patternOffset + 1 + offset.Length);
            footer = [.. footer.AsSpan(0, lengthFieldOffset),
                .. footer.AsSpan(lengthFieldOffset + 1 + length.Length)];
        }

        var result = new byte[checked((int)metadata.FooterOffset + footer.Length + 8)];
        bytes.AsSpan(0, checked((int)metadata.FooterOffset)).CopyTo(result);
        footer.CopyTo(result.AsSpan(checked((int)metadata.FooterOffset)));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(result.Length - 8), checked((uint)footer.Length));
        "PAR1"u8.CopyTo(result.AsSpan(result.Length - 4));
        return result;
    }

    static byte[] EncodeCompactInteger(long value)
    {
        var encoded = checked((ulong)value * 2);
        Span<byte> result = stackalloc byte[10];
        var length = 0;
        do
        {
            result[length++] = (byte)((encoded & 0x7f) | (encoded > 0x7f ? 0x80UL : 0UL));
            encoded >>= 7;
        }
        while (encoded != 0);
        return result[..length].ToArray();
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
