using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using TUnit.Assertions.Enums;
using ParquetDataPageVersion = Plank.Writing.ParquetDataPageVersion;

namespace Plank.Tests.E2E;

internal sealed class CustomValueConverterE2ETests
{
    static readonly CustomMappedValue[] Ids =
    [
        new(7),
        new(11),
        new(42),
        new(-3)
    ];

    static readonly CustomMappedValue?[] ParentIds =
    [
        null,
        new(7),
        new(11),
        null
    ];

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredFileValuesPreserveRequestedNullableConverter(ParquetDataPageVersion version)
    {
        var physical = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)]);
        var requested = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32,
                converter: new CustomMappedValueConverter())
        ]);
        int[] expected = [7, 11, 42, -3];
        using var output = new MemoryStream();
        using (var writer = physical.CreateWriter(output, new ParquetWriterOptions
               { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var column = writer.CreateSerializedColumn<int>(physical.LeafColumns[0]);
            column.Serialize(expected);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        using var input = new MemoryStream(output.ToArray());
        using var reader = requested.CreateReader(input);
        var values = new List<int?>();
        foreach (var buffer in reader.RowGroups[0].Column<CustomMappedValue?>(0))
            foreach (var value in buffer.Values)
                values.Add(value?.Value);
        if (!values.ToArray().AsSpan().SequenceEqual(expected.Select(value => (int?)value).ToArray()))
            throw new InvalidOperationException("Required-to-optional evolution lost the requested custom converter.");
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public async Task NullableNarrowConverterPreservesDeltaDefinitionLevels(ParquetDataPageVersion version)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]),
                converter: new NarrowMappedValueConverter())
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
               { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var column = writer.CreateSerializedColumn<CustomMappedValue?>(schema.LeafColumns[0]);
            column.Serialize(ParentIds);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }

        using var input = new MemoryStream(stream.ToArray());
        using var reader = schema.CreateReader(input);
        var actual = ReadColumn(reader.RowGroups[0].Column<CustomMappedValue?>(0));
        await Assert.That(actual.ToArray().AsSpan().SequenceEqual(ParentIds)).IsTrue();
    }

    [Test]
    public async Task RuntimeSchemaRoundTripsRequiredAndOptionalValues()
    {
        var converter = new CustomMappedValueConverter();
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]), converter: converter),
            ColumnDefinition.OptionalLeaf("parent_id", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]), converter: converter)
        ]);
        using var stream = new MemoryStream();

        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var ids = rowGroup.CreateSerializedColumn<CustomMappedValue>(schema.LeafColumns[0]);
        ids.Serialize(Ids);
        rowGroup.Write(ids);
        var parentIds = rowGroup.CreateSerializedColumn<CustomMappedValue?>(schema.LeafColumns[1]);
        parentIds.Serialize(ParentIds);
        rowGroup.Write(parentIds);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray());
        using var reader = schema.CreateReader(readStream);
        var actualIds = ReadColumn(reader.RowGroups[0].Column<CustomMappedValue>(schema.LeafColumns[0]));
        var actualParentIds = ReadColumn(reader.RowGroups[0].Column<CustomMappedValue?>(schema.LeafColumns[1]));

        await Assert.That(actualIds).IsEquivalentTo(Ids);
        await Assert.That(actualParentIds).IsEquivalentTo(ParentIds);
    }

    [Test]
    public async Task GeneratedColumnAndRowApisUseDeclaredConverter()
    {
        using var stream = new MemoryStream();
        var writer = CustomMappedRowSchema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Id.Serialize(Ids);
        rowGroup.Write(rowGroup.Id);
        rowGroup.ParentId.Serialize(ParentIds);
        rowGroup.Write(rowGroup.ParentId);
        writer.CloseFile();

        var fileBytes = stream.ToArray();
        using (var readStream = new MemoryStream(fileBytes))
        using (var reader = CustomMappedRowSchema.CreateReader(readStream))
        {
            var actualIds = ReadColumn(reader.RowGroups[0].IdColumn);
            var actualParentIds = ReadColumn(reader.RowGroups[0].ParentIdColumn);
            await Assert.That(actualIds).IsEquivalentTo(Ids);
            await Assert.That(actualParentIds).IsEquivalentTo(ParentIds);
        }

        using var rowReadStream = new MemoryStream(fileBytes);
        using var rowReader = CustomMappedRowSchema.CreateRowReader(rowReadStream);
        var rows = new List<(CustomMappedValue Id, CustomMappedValue? ParentId)>();
        while (rowReader.MoveNext())
            rows.Add((rowReader.Current.Id, rowReader.Current.ParentId));

        await Assert.That(rows).IsEquivalentTo(Ids.Zip(ParentIds));
    }

    [Test]
    public async Task GeneratedGuidAccessorsUseDeclaredConverter()
    {
        Guid[] ids = [GuidIndexConverter.Table[2], GuidIndexConverter.Table[0], GuidIndexConverter.Table[1]];
        Guid?[] values = [GuidIndexConverter.Table[1], null, GuidIndexConverter.Table[2]];
        using var stream = new MemoryStream();
        using (var writer = GuidConverterRows.CreateRowWriter(stream, maxParallelism: 1))
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var row = writer.GetRow();
                row.Id = ids[i];
                row.Value = values[i];
            }
            writer.Complete();
        }

        var bytes = stream.ToArray();
        using var columns = GuidConverterRows.CreateReader(new MemoryStream(bytes));
        await Assert.That(ReadColumn(columns.RowGroups[0].IdColumn)).IsEquivalentTo(ids, CollectionOrdering.Matching);
        await Assert.That(ReadColumn(columns.RowGroups[0].ValueColumn)).IsEquivalentTo(values, CollectionOrdering.Matching);
        using var rows = GuidConverterRows.CreateRowReader(new MemoryStream(bytes));
        var read = new List<(Guid, Guid?)>();
        while (rows.MoveNext())
            read.Add((rows.Current.Id, rows.Current.Value));
        await Assert.That(read).IsEquivalentTo(ids.Zip(values), CollectionOrdering.Matching);
    }

    [Test]
    public async Task GeneratedRowWriterProducesPhysicalInt32Columns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-custom-converter-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var stream = File.Create(path))
            {
                using var writer = CustomMappedRowSchema.CreateRowWriter(stream);
                for (var i = 0; i < Ids.Length; i++)
                {
                    var row = writer.GetRow();
                    row.Id = Ids[i];
                    row.ParentId = ParentIds[i];
                }
                writer.Complete();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroup = reader.RowGroup(0);
            var ids = rowGroup.Column(0).LogicalReader<int>().ReadAll(Ids.Length);
            var parentIds = rowGroup.Column(1).LogicalReader<int?>().ReadAll(ParentIds.Length);
            await Assert.That(ids).IsEquivalentTo(Ids.Select(static value => value.Value));
            await Assert.That(parentIds).IsEquivalentTo(ParentIds.Select(static value => value?.Value));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task GuidPhysicalMappingsRoundTripByteStreamSplitAndDictionaryPages()
    {
        CustomGuidValue[] ids =
        [
            new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            new(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")),
            new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"))
        ];
        CustomGuidValue?[] parentIds = [null, ids[0], ids[1]];
        var converter = new CustomGuidValueConverter();
        var fixedLengthOptions = new ColumnOptions(
            encodings: [EncodingKind.ByteStreamSplit], typeLength: 16);
        var optionalDictionaryOptions = new ColumnOptions(
            encodings: [EncodingKind.RleDictionary], typeLength: 16);
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.FixedLenByteArray,
                fixedLengthOptions, new Plank.Schema.LogicalType.Uuid(), converter: converter),
            ColumnDefinition.OptionalLeaf("parent_id", ParquetPhysicalType.FixedLenByteArray,
                optionalDictionaryOptions, new Plank.Schema.LogicalType.Uuid(), converter: converter)
        ]);
        using var stream = new MemoryStream();

        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var idColumn = rowGroup.CreateSerializedColumn<CustomGuidValue>(schema.LeafColumns[0]);
        idColumn.Serialize(ids);
        rowGroup.Write(idColumn);
        var parentIdColumn = rowGroup.CreateSerializedColumn<CustomGuidValue?>(schema.LeafColumns[1]);
        parentIdColumn.Serialize(parentIds);
        rowGroup.Write(parentIdColumn);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray());
        using var reader = schema.CreateReader(readStream);
        var actualIds = ReadColumn(reader.RowGroups[0].Column<CustomGuidValue>(schema.LeafColumns[0]));
        var actualParentIds = ReadColumn(reader.RowGroups[0].Column<CustomGuidValue?>(schema.LeafColumns[1]));

        await Assert.That(actualIds).IsEquivalentTo(ids);
        await Assert.That(actualParentIds).IsEquivalentTo(parentIds);
    }

    [Test]
    public async Task DecimalMappingRoundTripsScaledInt64Values()
    {
        decimal[] amounts = [12.34m, -0.01m, 999999.99m];
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("amount", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]),
                new Plank.Schema.LogicalType.Decimal(18, 2), converter: new ScaledDecimalConverter())
        ]);
        using var stream = new MemoryStream();

        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var amountColumn = rowGroup.CreateSerializedColumn<decimal>(schema.LeafColumns[0]);
        amountColumn.Serialize(amounts);
        rowGroup.Write(amountColumn);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray());
        using var reader = schema.CreateReader(readStream);
        var actual = ReadColumn(reader.RowGroups[0].Column<decimal>(schema.LeafColumns[0]));

        await Assert.That(actual).IsEquivalentTo(amounts);
    }

    [Test]
    public void GeneratedConverterRowReaderDoesNotAllocateAfterWarmup()
    {
        using var stream = new MemoryStream();
        var writer = CustomMappedRowSchema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Id.Serialize(Ids);
        rowGroup.Write(rowGroup.Id);
        rowGroup.ParentId.Serialize(ParentIds);
        rowGroup.Write(rowGroup.ParentId);
        writer.CloseFile();

        using var source = new Plank.Reading.MemoryReadSource(stream.ToArray());
        using var pool = new DefaultParquetBufferPool();
        using var reader = CustomMappedRowSchema.CreateRowReader(source, options: new()
        {
            BufferPool = pool,
            // Measure conversion on this thread. Worker completion waits can lazily
            // allocate even after warmup, and worker allocations escape this counter.
            Execution = new() { WorkerCount = 1 }
        });
        for (var i = 0; i < 8; i++)
        {
            reader.Reset(source);
            _ = SumRows(reader);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        reader.Reset(source);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = SumRows(reader);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected converted row reads to allocate zero bytes after warmup but saw {allocated} bytes.");
    }

    [Test]
    public async Task SchemaRejectsConverterWhosePhysicalTypeDoesNotMatchColumn()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await Task.Yield();
            _ = ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int64,
                converter: new CustomMappedValueConverter());
        });
    }

    [Test]
    public void SpanConversionDoesNotAllocate()
    {
        var converter = new CustomMappedValueConverter();
        Span<int> physical = stackalloc int[Ids.Length];
        Span<CustomMappedValue> values = stackalloc CustomMappedValue[Ids.Length];
        converter.ConvertToPhysical(Ids, physical);
        converter.ConvertFromPhysical(physical, values);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            converter.ConvertToPhysical(Ids, physical);
            converter.ConvertFromPhysical(physical, values);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated != 0)
            throw new InvalidOperationException($"Span conversion allocated {allocated} bytes.");
        if (!values.SequenceEqual(Ids))
            throw new InvalidOperationException("Span conversion did not round-trip values.");
    }

    static List<T> ReadColumn<T>(Plank.Reading.Logical.RowGroupColumn<T> column)
    {
        var values = new List<T>();
        foreach (var buffer in column)
            values.AddRange(buffer.Values);
        return values;
    }

    static long SumRows(CustomMappedRowSchema.RowReader reader)
    {
        long sum = 0;
        while (reader.MoveNext())
            sum += reader.Current.Id.Value + (reader.Current.ParentId?.Value ?? 0);
        return sum;
    }

    sealed class NarrowMappedValueConverter : ParquetValueConverter<CustomMappedValue, byte>
    {
        public override byte ConvertToPhysical(CustomMappedValue value)
            => checked((byte)value.Value);

        public override CustomMappedValue ConvertFromPhysical(byte value)
            => new(value);
    }

    readonly record struct CustomGuidValue(Guid Value);

    sealed class CustomGuidValueConverter : ParquetValueConverter<CustomGuidValue, Guid>
    {
        public override Guid ConvertToPhysical(CustomGuidValue value)
            => value.Value;

        public override CustomGuidValue ConvertFromPhysical(Guid value)
            => new(value);
    }

    sealed class ScaledDecimalConverter : ParquetValueConverter<decimal, long>
    {
        public override long ConvertToPhysical(decimal value)
            => decimal.ToInt64(value * 100m);

        public override decimal ConvertFromPhysical(long value)
            => value / 100m;
    }
}

[ParquetSchema]
internal sealed partial class GuidConverterRows
{
    [ParquetColumn(Converter = typeof(GuidIndexConverter))]
    public Guid Id { get; set; }

    [ParquetColumn(Converter = typeof(GuidIndexConverter))]
    public Guid? Value { get; set; }
}

internal sealed class GuidIndexConverter : ParquetValueConverter<Guid, int>
{
    internal static readonly Guid[] Table =
    [
        new("0f8fad5b-d9cb-469f-a165-70867728950e"),
        new("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
        new("e02fd0e4-00fd-090a-ca30-0d00a0038ba0")
    ];

    public override int ConvertToPhysical(Guid value)
    {
        var index = Array.IndexOf(Table, value);
        return index >= 0 ? 100 + index : throw new InvalidOperationException("Unexpected Guid value.");
    }

    public override Guid ConvertFromPhysical(int value) => Table[value - 100];
}
