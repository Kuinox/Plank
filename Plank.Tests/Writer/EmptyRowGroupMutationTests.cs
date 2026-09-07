using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Tests.Reading.ParquetTesting;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class EmptyRowGroupMutationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AppendsAfterEmptyRowGroup(bool appendLatest, bool precedingValues)
    {
        var schema = CreateSchema();
        using var file = new MemorySource([]);
        using (var writer = schema.CreateWriter(file))
        {
            if (precedingValues)
                WriteGroup(writer, schema, [1, 2]);
            WriteGroup(writer, schema, []);
            writer.CloseFile();
        }
        using (var writer = schema.CreateAppender(file, file,
                   new ParquetAppendOptions { AppendToLatestRowGroup = appendLatest }))
        {
            WriteGroup(writer, schema, [3, 4]);
            writer.CloseFile();
        }
        int[] expected = precedingValues ? [1, 2, 3, 4] : [3, 4];
        await Assert.That(ReadValues(file.Bytes, schema)).IsEquivalentTo(expected);
        using var physical = new ParquetFileReader();
        physical.Reset(file);
        await Assert.That(physical.Metadata.RowGroupCount)
            .IsEqualTo((precedingValues ? 1 : 0) + (appendLatest ? 1 : 2));
    }

    [Test]
    public async Task EmptyChunkWithAbsentOffsetDoesNotOverwriteHeaderWhenAppendingLatest()
    {
        var schema = CreateSchema();
        var empty = WriteFile(schema, []);
        SetFirstColumnField(empty, 9, 0);
        using var destination = new MemorySource(empty);
        using (var appender = schema.CreateAppender(destination, destination,
                   new ParquetAppendOptions { AppendToLatestRowGroup = true }))
        {
            WriteGroup(appender, schema, [3, 4]);
            appender.CloseFile();
        }
        await Assert.That(ReadValues(destination.Bytes, schema)).IsEquivalentTo([3, 4]);
    }

    [Test]
    [Arguments(5, 1)] // Nonzero value count cannot be stored in a zero-byte chunk.
    [Arguments(6, 1)] // Neither can a nonzero uncompressed byte count.
    [Arguments(9, 1)] // A nonzero offset cannot point inside the file magic.
    [Arguments(9, 63)] // Or past the data section, even for an empty chunk.
    public async Task MalformedEmptyChunkIsRejectedBeforeMutation(int field, byte value)
    {
        var schema = CreateSchema();
        var malformed = WriteFile(schema, []);
        SetFirstColumnField(malformed, field, value);
        using var source = new MemorySource(malformed);
        Assert.Throws<CorruptParquetException>(() => schema.CreateAppender(source, source));
        await Assert.That(source.Bytes.AsSpan().SequenceEqual(malformed)).IsTrue();
        byte[] original = [1, 2, 3];
        using var destination = new MemorySource(original);
        Assert.Throws<CorruptParquetException>(() => schema.CreateMerger(source, destination));
        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task MergesEmptyGroupsBeforeAndAfterPopulatedGroups(bool inPlace, bool absentDataOffset)
    {
        var schema = CreateSchema();
        var empty = WriteFile(schema, []);
        if (absentDataOffset)
            SetFirstColumnField(empty, 9, 0);
        var populated = WriteFile(schema, [1, 2]);
        using var destination = new MemorySource(inPlace ? empty : []);
        var merger = inPlace
            ? schema.CreateMerger(destination)
            : schema.CreateMerger(new MemoryReadSource(empty), destination);
        merger.AppendFile(new MemoryReadSource(populated));
        merger.AppendFile(new MemoryReadSource(empty));
        merger.CloseFile();
        await Assert.That(merger.RowCount).IsEqualTo(2L);
        await Assert.That(merger.RowGroupCount).IsEqualTo(3);
        await Assert.That(merger.SourceFileCount).IsEqualTo(3);
        await Assert.That(ReadValues(destination.Bytes, schema)).IsEquivalentTo([1, 2]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AppendsAndMergesArrowDictionaryOnlyGroups(bool appendLatest)
    {
        var bytes = ParquetTestingCorpus.ReadAllBytes("data/column_chunk_key_value_metadata.parquet");
        using var sourceReader = new ParquetReader();
        sourceReader.Reset(new MemoryReadSource(bytes));
        var schema = sourceReader.Schema;
        using var file = new MemoryStream();
        file.Write(bytes);
        using (var appender = schema.CreateAppender(file,
                   new ParquetAppendOptions { AppendToLatestRowGroup = appendLatest }))
            appender.CloseFile();
        using var destination = new MemorySource([]);
        var merger = schema.CreateMerger(new MemoryReadSource(file.ToArray()), destination);
        merger.AppendFile(new MemoryReadSource(bytes));
        merger.CloseFile();
        await Assert.That(merger.RowCount).IsEqualTo(0L);
        await Assert.That(merger.RowGroupCount).IsEqualTo(2);
        using var reader = schema.CreateReader(new MemoryReadSource(destination.Bytes));
        foreach (var group in reader.RowGroups)
            for (var column = 0; column < schema.LeafColumns.Length; column++)
            {
                using var pages = group.GetColumnMetadata(column).OpenPages();
                await Assert.That(pages.Count).IsEqualTo(0);
                var count = 0;
                foreach (var buffer in group.Column<int?>(column))
                    count += buffer.Count;
                await Assert.That(count).IsEqualTo(0);
            }
    }

    static ParquetSchema CreateSchema() => new([
        ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32)
    ]);

    static void SetFirstColumnField(byte[] bytes, int targetField, byte value)
    {
        using var physical = new ParquetFileReader();
        physical.Reset(new MemoryReadSource(bytes));
        var group = physical.Metadata.RowGroups[0];
        var groupOffset = checked((int)group.MetadataOffset);
        var reader = new CompactProtocolReader(bytes.AsSpan(groupOffset, group.MetadataLength));
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var field, out var type, out var inlineBool))
        {
            if (field != 1)
            {
                reader.Skip(type, inlineBool);
                continue;
            }
            _ = reader.ReadListHeader();
            reader.BeginStruct();
            while (reader.TryReadFieldHeader(out field, out type, out inlineBool))
            {
                if (field != 3)
                {
                    reader.Skip(type, inlineBool);
                    continue;
                }
                reader.BeginStruct();
                while (reader.TryReadFieldHeader(out field, out type, out inlineBool))
                {
                    if (field != targetField)
                    {
                        reader.Skip(type, inlineBool);
                        continue;
                    }
                    var offset = reader.Offset;
                    _ = reader.ReadI64();
                    if (reader.Offset != offset + 1 || value > 63)
                        throw new InvalidOperationException("Expected a single-byte integer fixture field.");
                    bytes[groupOffset + offset] = checked((byte)(value * 2));
                    return;
                }
            }
        }
        throw new InvalidOperationException("The fixture column field was not found.");
    }

    static byte[] WriteFile(ParquetSchema schema, int[] values)
    {
        using var stream = new MemoryStream();
        using var writer = schema.CreateWriter(stream);
        WriteGroup(writer, schema, values);
        writer.CloseFile();
        return stream.ToArray();
    }

    static void WriteGroup(ParquetWriter writer, ParquetSchema schema, int[] values)
    {
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
    }

    static int[] ReadValues(byte[] bytes, ParquetSchema schema)
    {
        using var reader = schema.CreateReader(new MemoryReadSource(bytes));
        var values = new List<int>();
        foreach (var group in reader.RowGroups)
            foreach (var buffer in group.Column<int>(0))
                values.AddRange(buffer.Values);
        return values.ToArray();
    }

    sealed class MemorySource(byte[] bytes) : IParquetReadWriteSource
    {
        readonly MemoryStream _stream = CreateStream(bytes);
        public byte[] Bytes => _stream.ToArray();
        public ulong Length => checked((ulong)_stream.Length);
        public void Open(ReadOnlySpan<byte> path, FileMode mode) => throw new NotSupportedException();
        public void Close() { }
        public void Flush() => _stream.Flush();
        public void Dispose() => _stream.Dispose();
        public void SetLength(ulong length) => _stream.SetLength(checked((long)length));
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
        static MemoryStream CreateStream(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes);
            return stream;
        }
    }
}
