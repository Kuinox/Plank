using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class MutationSchemaIntegrityTests
{
    [Test]
    [Arguments("subset")]
    [Arguments("extra field")]
    [Arguments("reordered")]
    [Arguments("required to optional")]
    [Arguments("optional to required")]
    [Arguments("nested repetition")]
    [Arguments("nested order")]
    [Arguments("nested field ID")]
    [Arguments("leaf field ID")]
    [Arguments("removed field ID")]
    [Arguments("logical type")]
    [Arguments("fixed byte length")]
    [Arguments("list element repetition")]
    [Arguments("list depth")]
    [Arguments("map value repetition")]
    [Arguments("map field ID")]
    public async Task IncompatibleSchemaIsRejectedBeforeAnyMutation(string scenario)
    {
        var (fileSchema, requestedSchema) = Schemas(scenario);
        var original = WriteFile(fileSchema, scenario is "subset" or "extra field" or "reordered");
        using var existing = new MemorySource(original);
        foreach (var appendLatest in new[] { false, true })
        {
            Assert.Throws<InvalidOperationException>(() => requestedSchema.CreateAppender(existing, existing,
                new ParquetAppendOptions { AppendToLatestRowGroup = appendLatest }));
            await Assert.That(existing.Bytes.AsSpan().SequenceEqual(original)).IsTrue();
        }
        Assert.Throws<InvalidOperationException>(() => requestedSchema.CreateMerger(existing));
        await Assert.That(existing.Bytes.AsSpan().SequenceEqual(original)).IsTrue();

        byte[] destinationBytes = [7, 8, 9, 10];
        using var destination = new MemorySource(destinationBytes);
        Assert.Throws<InvalidOperationException>(() => requestedSchema.CreateAppender(existing, destination));
        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(destinationBytes)).IsTrue();
        Assert.Throws<InvalidOperationException>(() => requestedSchema.CreateMerger(existing, destination));
        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(destinationBytes)).IsTrue();
        await Assert.That(existing.Bytes.AsSpan().SequenceEqual(original)).IsTrue();

        var validBytes = WriteFile(requestedSchema);
        using var mergeDestination = new MemorySource(validBytes);
        var merger = requestedSchema.CreateMerger(mergeDestination);
        var beforeImport = mergeDestination.Bytes;
        Assert.Throws<InvalidOperationException>(() => merger.AppendFile(existing));
        await Assert.That(mergeDestination.Bytes.AsSpan().SequenceEqual(beforeImport)).IsTrue();
        await Assert.That(merger.SourceFileCount).IsEqualTo(1);
        await Assert.That(merger.RowCount).IsEqualTo(0L);
        merger.AppendFile(new MemoryReadSource(validBytes));
        merger.CloseFile();
        using var reader = requestedSchema.CreateReader(new MemoryReadSource(mergeDestination.Bytes));
        await Assert.That(reader.RowGroups.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamAppenderRejectsSubsetWithoutChangingBytes()
    {
        var (fileSchema, requestedSchema) = Schemas("subset");
        var bytes = WriteFile(fileSchema, withValues: true);
        using var stream = new MemoryStream();
        stream.Write(bytes);
        Assert.Throws<InvalidOperationException>(() => requestedSchema.CreateAppender(stream));
        await Assert.That(stream.ToArray().AsSpan().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    public async Task SchemaDependentWriterOptionErrorsDoNotClearMergeDestination()
    {
        var schema = new ParquetSchema([Int("A")]);
        using var source = new MemorySource(WriteFile(schema, withValues: true));
        byte[] original = [1, 2, 3, 4, 5];
        using var destination = new MemorySource(original);
        Assert.Throws<ArgumentOutOfRangeException>(() => schema.CreateMerger(source, destination,
            new ParquetMergeOptions
            {
                WriterOptions = new ParquetWriterOptions { SortingColumns = [new ParquetSortingColumn(1)] }
            }));
        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task FailedSeparateAppenderCopyLeavesSourceUsable()
    {
        var schema = new ParquetSchema([Int("A"), Int("B")]);
        var bytes = WriteFile(schema, withValues: true);
        using var source = new MemorySource(bytes);
        byte[] original = [9, 8, 7];
        using var destination = new MemorySource(original) { RejectWrites = true };
        Assert.Throws<IOException>(() => schema.CreateAppender(source, destination));
        await Assert.That(source.Bytes.AsSpan().SequenceEqual(bytes)).IsTrue();
        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(original)).IsTrue();
        destination.RejectWrites = false;
        using var appender = schema.CreateAppender(source, destination);
        WriteValues(appender, schema, 100);
        appender.CloseFile();
        await CheckFlatValues(destination.Bytes, schema, [11, 111], [22, 122]).ConfigureAwait(false);
    }

    [Test]
    public async Task LatestGroupReplacementDoesNotTrustAdvisoryFileOffset()
    {
        var schema = new ParquetSchema([Int("A"), Int("B")]);
        var bytes = WriteFile(schema, withValues: true);
        using (var reader = new Plank.Reading.Physical.ParquetFileReader())
        {
            reader.Reset(new MemoryReadSource(bytes));
            var group = reader.Metadata.RowGroups[0];
            var metadataBytes = bytes.AsSpan(checked((int)group.MetadataOffset), group.MetadataLength);
            // This fixture starts at offset 4; replace the row-group field 5 value with offset 1.
            var offsetField = metadataBytes.LastIndexOf((ReadOnlySpan<byte>)[0x26, 0x08]);
            if (offsetField < 0)
                throw new InvalidOperationException("The fixture's row-group file_offset was not found.");
            metadataBytes[offsetField + 1] = 0x02;
        }
        using var source = new MemorySource(bytes);
        using var appender = schema.CreateAppender(source, source,
            new ParquetAppendOptions { AppendToLatestRowGroup = true });
        WriteValues(appender, schema, 100);
        appender.CloseFile();
        await Assert.That(source.Bytes.AsSpan(0, 4).SequenceEqual("PAR1"u8)).IsTrue();
        await CheckFlatValues(source.Bytes, schema, [11, 111], [22, 122]).ConfigureAwait(false);
    }

    [Test]
    public async Task LatestGroupReplacementPreservesPhysicallyLaterRetainedGroups()
    {
        var schema = new ParquetSchema([Int("A"), Int("B")]);
        using var original = new MemorySource();
        using (var writer = schema.CreateWriter(original))
        {
            WriteValues(writer, schema, 0);
            WriteValues(writer, schema, 100);
            writer.CloseFile();
        }
        var bytes = original.Bytes;
        using (var reader = new Plank.Reading.Physical.ParquetFileReader())
        {
            reader.Reset(new MemoryReadSource(bytes));
            var first = reader.Metadata.RowGroups[0];
            var second = reader.Metadata.RowGroups[1];
            // Row-group metadata order need not follow physical page order.
            var firstBytes = bytes.AsSpan(checked((int)first.MetadataOffset), first.MetadataLength).ToArray();
            var secondBytes = bytes.AsSpan(checked((int)second.MetadataOffset), second.MetadataLength).ToArray();
            secondBytes.CopyTo(bytes.AsSpan(checked((int)first.MetadataOffset)));
            firstBytes.CopyTo(bytes.AsSpan(checked((int)first.MetadataOffset) + second.MetadataLength));
        }
        using var destination = new MemorySource(bytes);
        using var appender = schema.CreateAppender(destination, destination,
            new ParquetAppendOptions { AppendToLatestRowGroup = true });
        WriteValues(appender, schema, 1000);
        appender.CloseFile();
        await CheckFlatValues(destination.Bytes, schema, [111, 11, 1011], [122, 22, 1022]).ConfigureAwait(false);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SeparateAppenderDestinationRetainsSourceValues(bool appendLatest)
    {
        var schema = new ParquetSchema([Int("A"), Int("B")]);
        var bytes = WriteFile(schema, withValues: true);
        using var source = new MemorySource(bytes);
        using var destination = new MemorySource([9, 8, 7]);
        using var appender = schema.CreateAppender(source, destination,
            new ParquetAppendOptions { AppendToLatestRowGroup = appendLatest });
        WriteValues(appender, schema, 100);
        appender.CloseFile();
        await Assert.That(source.Bytes.AsSpan().SequenceEqual(bytes)).IsTrue();
        await CheckFlatValues(destination.Bytes, schema, [11, 111], [22, 122]).ConfigureAwait(false);
    }

    [Test]
    public async Task SamePhysicalSchemaAllowsDifferentWriterSettings()
    {
        var schema = new ParquetSchema([Int("A"), Int("B")]);
        var requested = new ParquetSchema([
            Int("A") with { Options = new ColumnOptions(compression: CompressionKind.Gzip) },
            Int("B") with { Options = new ColumnOptions(encodings: [EncodingKind.RleDictionary]) }
        ]);
        var bytes = WriteFile(schema, withValues: true);
        using var destination = new MemorySource(bytes);
        using (var appender = requested.CreateAppender(destination, destination))
        {
            WriteValues(appender, requested, 100);
            appender.CloseFile();
        }
        var merger = requested.CreateMerger(destination);
        merger.AppendFile(new MemoryReadSource(bytes));
        merger.CloseFile();
        await CheckFlatValues(destination.Bytes, schema, [11, 111, 11], [22, 122, 22]).ConfigureAwait(false);
    }

    [Test]
    public async Task CanonicalListWithFieldIdsRetainsValuesAcrossAppendAndMerge()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("values", ColumnDefinition.RequiredLeaf("item", ParquetPhysicalType.Int32, 8), 7)
        ]);
        using var source = new MemorySource();
        using (var writer = schema.CreateWriter(source))
        {
            WriteLists(writer, schema);
            writer.CloseFile();
        }
        var original = source.Bytes;
        using (var appender = schema.CreateAppender(source, source))
        {
            WriteLists(appender, schema);
            appender.CloseFile();
        }
        var merger = schema.CreateMerger(source);
        merger.AppendFile(new MemoryReadSource(original));
        merger.CloseFile();
        using var reader = new ParquetReader();
        reader.Reset(new MemoryReadSource(source.Bytes));
        await Assert.That(reader.Schema.Definitions[0].FieldId).IsEqualTo(7);
        await Assert.That(reader.Schema.LeafColumns[0].FieldId).IsEqualTo(8);
        await Assert.That(reader.RowGroups.Count).IsEqualTo(3);
        foreach (var group in reader.RowGroups)
        {
            var values = new List<int>();
            var definitions = new List<int>();
            var repetitions = new List<int>();
            foreach (var buffer in group.NestedColumn<int>(0))
            {
                values.AddRange(buffer.Values.Values);
                definitions.AddRange(buffer.DefinitionLevels);
                repetitions.AddRange(buffer.RepetitionLevels);
            }
            await Assert.That(values.ToArray().AsSpan().SequenceEqual([1, 2, 3])).IsTrue();
            await Assert.That(definitions.ToArray().AsSpan().SequenceEqual([1, 1, 0, 1])).IsTrue();
            await Assert.That(repetitions.ToArray().AsSpan().SequenceEqual([0, 1, 0, 0])).IsTrue();
        }
    }

    [Test]
    public async Task MergeRejectsDifferentBuiltInAdaptersForTheSameStream()
    {
        var schema = new ParquetSchema([Int("A")]);
        var original = WriteFile(schema, withValues: true);
        using var stream = new MemoryStream();
        stream.Write(original);
        using var source = new StreamReadSource(stream);
        using var destination = new StreamParquetSource(stream);
        Assert.Throws<ArgumentException>(() => schema.CreateMerger(source, destination));
        await Assert.That(stream.ToArray().AsSpan().SequenceEqual(original)).IsTrue();

        var merger = schema.CreateMerger(destination);
        var before = stream.ToArray();
        Assert.Throws<ArgumentException>(() => merger.AppendFile(source));
        await Assert.That(stream.ToArray().AsSpan().SequenceEqual(before)).IsTrue();
        merger.CloseFile();
    }

    static (ParquetSchema File, ParquetSchema Requested) Schemas(string scenario)
    {
        var a = Int("A");
        var b = Int("B");
        return scenario switch
        {
            "subset" => (new([a, b]), new([a])),
            "extra field" => (new([a]), new([a, b])),
            "reordered" => (new([a, b]), new([b, a])),
            "required to optional" => (new([a]), new([ColumnDefinition.OptionalLeaf("A", ParquetPhysicalType.Int32)])),
            "optional to required" => (new([ColumnDefinition.OptionalLeaf("A", ParquetPhysicalType.Int32)]), new([a])),
            "nested repetition" => (
                new([ColumnDefinition.OptionalGroup("parent", a)]),
                new([ColumnDefinition.RequiredGroup("parent", ColumnDefinition.OptionalLeaf("A", ParquetPhysicalType.Int32))])),
            "nested order" => (new([ColumnDefinition.RequiredGroup("parent", a, b)]),
                new([ColumnDefinition.RequiredGroup("parent", b, a)])),
            "nested field ID" => (new([ColumnDefinition.RequiredGroup("parent", 1, a)]),
                new([ColumnDefinition.RequiredGroup("parent", 2, a)])),
            "leaf field ID" => (new([a with { FieldId = 1 }]), new([a with { FieldId = 2 }])),
            "removed field ID" => (new([a with { FieldId = 1 }]), new([a])),
            "logical type" => (new([a with { LogicalType = new LogicalType.Int(32, true) }]), new([a])),
            "fixed byte length" => (
                new([ColumnDefinition.RequiredLeaf("A", ParquetPhysicalType.FixedLenByteArray, new ColumnOptions(typeLength: 4))]),
                new([ColumnDefinition.RequiredLeaf("A", ParquetPhysicalType.FixedLenByteArray, new ColumnOptions(typeLength: 8))])),
            "list element repetition" => (new([ColumnDefinition.List("A", Int("element"))]),
                new([ColumnDefinition.List("A", ColumnDefinition.OptionalLeaf("element", ParquetPhysicalType.Int32))])),
            "list depth" => (new([ColumnDefinition.List("A", Int("element"))]),
                new([ColumnDefinition.List("A", ColumnDefinition.List("element", Int("element")))])),
            "map value repetition" => (new([ColumnDefinition.Map("A", Int("key"), Int("value"))]),
                new([ColumnDefinition.Map("A", Int("key"), ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32))])),
            "map field ID" => (new([ColumnDefinition.Map("A", Int("key"), Int("value"), 1)]),
                new([ColumnDefinition.Map("A", Int("key"), Int("value"), 2)])),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    static ColumnDefinition Int(string name) => ColumnDefinition.RequiredLeaf(name, ParquetPhysicalType.Int32);

    static byte[] WriteFile(ParquetSchema schema, bool withValues = false)
    {
        using var source = new MemorySource();
        using var writer = schema.CreateWriter(source);
        if (withValues)
            WriteValues(writer, schema, 0);
        writer.CloseFile();
        return source.Bytes;
    }

    static void WriteValues(ParquetWriter writer, ParquetSchema schema, int offset)
    {
        var group = writer.StartRowGroup();
        for (var ordinal = 0; ordinal < schema.LeafColumns.Length; ordinal++)
        {
            var column = group.CreateSerializedColumn<int>(schema.LeafColumns[ordinal]);
            column.Serialize([offset + (ordinal + 1) * 11]);
            group.Write(column);
        }
    }

    static void WriteLists(ParquetWriter writer, ParquetSchema schema)
    {
        var group = writer.StartRowGroup();
        var column = group.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
        column.Serialize([[1, 2], [], [3]]);
        group.Write(column);
    }

    static async Task CheckFlatValues(byte[] bytes, ParquetSchema schema, int[] expectedA, int[] expectedB)
    {
        using var reader = schema.CreateReader(new MemoryReadSource(bytes));
        foreach (var (ordinal, expected) in new[] { (0, expectedA), (1, expectedB) })
        {
            var values = new List<int>();
            foreach (var group in reader.RowGroups)
                foreach (var buffer in group.Column<int>(ordinal))
                    values.AddRange(buffer.Values);
            await Assert.That(values.ToArray().AsSpan().SequenceEqual(expected)).IsTrue();
        }
    }

    sealed class MemorySource : IParquetReadWriteSource
    {
        readonly MemoryStream _stream = new();
        internal MemorySource(byte[]? bytes = null)
        {
            if (bytes is not null)
                _stream.Write(bytes);
        }
        internal byte[] Bytes => _stream.ToArray();
        internal bool RejectWrites { get; set; }
        public ulong Length => checked((ulong)_stream.Length);
        public void Open(ReadOnlySpan<byte> path, FileMode mode) => throw new NotSupportedException();
        public void Close() { }
        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            _stream.Position = checked((long)offset);
            _stream.ReadExactly(destination);
        }
        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            if (RejectWrites)
                throw new IOException("Injected destination failure.");
            _stream.Position = checked((long)offset);
            _stream.Write(source);
        }
        public void SetLength(ulong length) => _stream.SetLength(checked((long)length));
        public void Flush() => _stream.Flush();
        public void Dispose() => _stream.Dispose();
    }
}
