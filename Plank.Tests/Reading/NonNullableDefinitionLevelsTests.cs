using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class NonNullableDefinitionLevelsTests
{
    static readonly string[] Samples = [
        "UEFSMRUGFVQVVFwVChUAFQoVEhUEFQASHBgI+NoXoZ8BAAAYCAAAAAAAAAAAFgAoCPjaF6GfAQAAGAgAAAAAAAAAABERAAAAFQIZLEgGc2NoZW1hFQIAFQQlAhgBYyUSbIwSHBwAAAAAABYKGRwZHCYAHBUEGSUSBhkYAWMVABYKFtwBFtwBJgg8GAj42hehnwEAABgIAAAAAAAAAAAWACgI+NoXoZ8BAAAYCAAAAAAAAAAAEREAABaiAhUWFuQBFT4AFtwBFgomCBbcAQA5HBwAAACMAAAAUEFSMQ==",
        "UEFSMRUGFVQVVFwVChUAFQoVEhUEFQASHBgI+NoXoZ8BAAAYCAAAAAAAAAAAFgAoCPjaF6GfAQAAGAgAAAAAAAAAABERAAAAABkcABUCGSxIBnNjaGVtYRUCABUEJQIYAWMlEmyMEhw8AAAAAAAWChkcGRwmABwVBBklEgYZGAFjFQQWChbcARbcASYIPBgI+NoXoZ8BAAAYCAAAAAAAAAAAFgAoCPjaF6GfAQAAGAgAAAAAAAAAABERAAAWogIVFhbkARU+ABbcARYKJggW3AEAORwcAAAAjAAAAFBBUjE=",
        "UEFSMRUGFSwVLFwVChUAFQoVABUEFQASHDYAKAT/////GAQAAAAAEREAAAAK//8VAhksSAZzY2hlbWEVAgAVAiUCGAFjJRpsrBMgEgAAABYKGRwZHCYAHBUCGSUABhkYAWMVABYKFnwWfCYIPDYAKAT/////GAQAAAAAEREAABayARUUFoQBFS4AFnwWCiYIFnwAORwcAAAAagAAAFBBUjE=",
        "UEFSMRUGFSwVLFwVChUAFQoVEhUEFQASHBgE/P8AABgEAAAAABYAKAT//wAAGAQAAAAAEREAAAAVAhksSAZzY2hlbYQVAgAVAiUCGAFjJQxsrBMQEgAAABYKGRwZHCYAHBUCGSUSBhkYAWMVABYKFpQBFpQBJgg8GAT//wAAGAQAAAAAFgAoBP//AAAYBAAAAAAREQAAFsoBChYWnAEVLgAWlAEWCiYIFpQBADkcHAAAAHoAAABQQVIx"
    ];

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)]
    public void CorruptSamplesRejectAllReaders(int sample)
    {
        var file = Convert.FromBase64String(Samples[sample]);
        if (sample < 2)
        {
            CheckCorrupt<long, long?>(file);
            CheckCorrupt<DateTime, DateTime?>(file);
            CheckCorrupt<DateTimeOffset, DateTimeOffset?>(file);
        }
        else if (sample == 2) CheckCorrupt<int, int?>(file);
        else CheckCorrupt<DateOnly, DateOnly?>(file);
    }

    static void CheckCorrupt<T, TN>(byte[] file)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));
        Assert.Throws<CorruptParquetException>(() => { foreach (var b in reader.RowGroups[0].Column<T>(0)) _ = b.Count; });
        Assert.Throws<CorruptParquetException>(() => { foreach (var b in reader.RowGroups[0].Column<TN>(0)) _ = b.Count; });
        var schema = reader.Schema;
        Assert.Throws<CorruptParquetException>(() =>
        {
            using var rows = new RowReaderCore(new MemoryStream(file, writable: false), schema,
                [new RowApiColumnDescriptor<T>("c", schema.LeafColumns[0])], null,
                new RowReaderOptions { Execution = new() { WorkerCount = 1 } }, null);
            while (rows.MoveNext()) { }
        });
    }

    [Test]
    [Arguments(EncodingKind.Plain)]
    [Arguments(EncodingKind.DeltaBinaryPacked)]
    public void V2AllPresentLiteralLevelsCountOnlyMasksPadding(EncodingKind encoding)
    {
        var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("c", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [encoding]))]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V2 }))
        {
            var group = writer.StartRowGroup();
            var column = group.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
            column.Serialize([1, 2, 3, 4, 5]);
            group.Write(column);
            writer.CloseFile();
        }
        var file = output.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var header = PageHeaderReader.Read(file.AsSpan(offset));
        if (header.Encoding != encoding || header.DefinitionLevelsByteLength != 2 || header.NullCount != 0)
            throw new InvalidOperationException("Unexpected page layout.");
        var level = offset + header.HeaderLength;
        file[level] = 3; // one literal group, five levels and three padding bits
        file[level + 1] = 0x1f;
        using (var reader = new ParquetReader())
        {
            reader.Reset(new MemoryStream(file, writable: false));
            var values = new List<int?>();
            foreach (var b in reader.RowGroups[0].Column<int?>(0)) values.AddRange(b.Values);
            if (!values.SequenceEqual(new int?[] { 1, 2, 3, 4, 5 })) throw new InvalidOperationException("Values changed.");
            var required = new List<int>();
            foreach (var b in reader.RowGroups[0].Column<int>(0)) required.AddRange(b.Values);
            if (!required.SequenceEqual([1, 2, 3, 4, 5])) throw new InvalidOperationException("Values changed.");
        }
        file[level + 1] = 0x0f; // the final actual level is null, contradicting the V2 header
        using var corrupt = new ParquetReader();
        corrupt.Reset(new MemoryStream(file, writable: false));
        Assert.Throws<CorruptParquetException>(() => { foreach (var b in corrupt.RowGroups[0].Column<int?>(0)) _ = b.Count; });
        Assert.Throws<CorruptParquetException>(() => { foreach (var b in corrupt.RowGroups[0].Column<int>(0)) _ = b.Count; });
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void ValidNullStillRejectsNonNullable(ParquetDataPageVersion version)
    {
        var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("c", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]))]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var group = writer.StartRowGroup();
            var column = group.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
            column.Serialize([1, null, 3]);
            group.Write(column);
            writer.CloseFile();
        }
        var file = output.ToArray();
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));
        var nullable = new List<int?>();
        foreach (var b in reader.RowGroups[0].Column<int?>(0)) nullable.AddRange(b.Values);
        if (!nullable.SequenceEqual(new int?[] { 1, null, 3 })) throw new InvalidOperationException("Nullable values changed.");
        Assert.Throws<NotSupportedException>(() => { foreach (var b in reader.RowGroups[0].Column<int>(0)) _ = b.Count; });
        Assert.Throws<NotSupportedException>(() =>
        {
            using var rows = new RowReaderCore(new MemoryStream(file, writable: false), schema,
                [new RowApiColumnDescriptor<int>("c", schema.LeafColumns[0])], null,
                new RowReaderOptions { Execution = new() { WorkerCount = 1 } }, null);
            while (rows.MoveNext()) { }
        });
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.Plain)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.Plain)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.DeltaBinaryPacked)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.DeltaBinaryPacked)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.ByteStreamSplit)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.ByteStreamSplit)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.RleDictionary)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.RleDictionary)]
    public void OptionalPageLevelsValidatedBeforeValues(ParquetDataPageVersion version, EncodingKind encoding)
    {
        var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("c", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [encoding]))]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var group = writer.StartRowGroup();
            var column = group.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
            column.Serialize(Enumerable.Range(0, 100).Select(i => (int?)(i % 2 + 1)).ToArray());
            group.Write(column);
            writer.CloseFile();
        }
        var file = output.ToArray();
        using (var reader = new ParquetReader())
        {
            reader.Reset(new MemoryStream(file, writable: false));
            var values = new List<int>();
            foreach (var b in reader.RowGroups[0].Column<int>(0)) values.AddRange(b.Values);
            if (!values.SequenceEqual(Enumerable.Range(0, 100).Select(i => i % 2 + 1)))
                throw new InvalidOperationException("Values changed.");
        }
        using (var rows = new RowReaderCore(new MemoryStream(file, writable: false), schema,
            [new RowApiColumnDescriptor<int>("c", schema.LeafColumns[0])], null,
            new RowReaderOptions { Execution = new() { WorkerCount = 1 } }, null))
        {
            var count = 0;
            while (rows.MoveNext())
            {
                if (rows.GetCurrent<int>(0) != count % 2 + 1) throw new InvalidOperationException("Row value changed.");
                count++;
            }
            if (count != 100) throw new InvalidOperationException("Row count changed.");
        }
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var header = PageHeaderReader.Read(file.AsSpan(offset));
        if (header.Encoding != encoding && !(encoding == EncodingKind.RleDictionary && header.Encoding == EncodingKind.PlainDictionary))
            throw new InvalidOperationException($"Expected {encoding}, got {header.Encoding}.");
        var level = offset + header.HeaderLength + (version == ParquetDataPageVersion.V1 ? 4 : 0);
        file[level] = 0; // empty RLE run
        Assert.Throws<CorruptParquetException>(() =>
        {
            using var reader = new ParquetReader();
            reader.Reset(new MemoryStream(file, writable: false));
            foreach (var b in reader.RowGroups[0].Column<int>(0)) _ = b.Count;
        });
    }
}
