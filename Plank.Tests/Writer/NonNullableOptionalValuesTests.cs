using Plank.Reading;
using Plank.Schema;
using Plank.Tests.E2E;
using Plank.Writing;

namespace Plank.Tests.Writer;

/// <summary>
/// Non-nullable values written to a leaf that carries definition levels — an optional leaf, or a required leaf
/// under an optional group — are all present. The pages used to be written without the definition levels the
/// column declares, so neither Plank nor Arrow could read the file back.
/// </summary>
internal sealed class NonNullableOptionalValuesTests
{
    internal enum Shape
    {
        OptionalLeaf,
        RequiredLeafInOptionalGroup,
        OptionalLeafInOptionalGroup
    }

    static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    [MatrixDataSource]
    public void Int32ValuesAreWrittenWithDefinitionLevels(
        [Matrix(Shape.OptionalLeaf, Shape.RequiredLeafInOptionalGroup, Shape.OptionalLeafInOptionalGroup)] Shape shape,
        [Matrix(ParquetDataPageVersion.V1, ParquetDataPageVersion.V2)] ParquetDataPageVersion version,
        [Matrix(EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked,
            EncodingKind.ByteStreamSplit)] EncodingKind encoding,
        [Matrix(false, true)] bool smallPages)
        => AssertRoundTrip(shape, version, encoding, smallPages, ParquetPhysicalType.Int32, logicalType: null,
            CreateValues(static value => value));

    [Test]
    [MatrixDataSource]
    public void FloatValuesAreWrittenWithDefinitionLevels(
        [Matrix(Shape.OptionalLeaf, Shape.RequiredLeafInOptionalGroup, Shape.OptionalLeafInOptionalGroup)] Shape shape,
        [Matrix(ParquetDataPageVersion.V1, ParquetDataPageVersion.V2)] ParquetDataPageVersion version,
        [Matrix(EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.ByteStreamSplit)] EncodingKind encoding,
        [Matrix(false, true)] bool smallPages)
        => AssertRoundTrip(shape, version, encoding, smallPages, ParquetPhysicalType.Float, logicalType: null,
            CreateValues(static value => value * 0.5f));

    [Test]
    [MatrixDataSource]
    public void DecimalValuesAreWrittenWithDefinitionLevels(
        [Matrix(Shape.OptionalLeaf, Shape.RequiredLeafInOptionalGroup, Shape.OptionalLeafInOptionalGroup)] Shape shape,
        [Matrix(ParquetDataPageVersion.V1, ParquetDataPageVersion.V2)] ParquetDataPageVersion version,
        [Matrix(EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked)] EncodingKind encoding,
        [Matrix(false, true)] bool smallPages)
        => AssertRoundTrip(shape, version, encoding, smallPages, ParquetPhysicalType.Int64,
            new LogicalType.Decimal(18, 3), CreateValues(static value => value * 1.125m));

    [Test]
    [MatrixDataSource]
    public void DateTimeValuesAreWrittenWithDefinitionLevels(
        [Matrix(Shape.OptionalLeaf, Shape.RequiredLeafInOptionalGroup, Shape.OptionalLeafInOptionalGroup)] Shape shape,
        [Matrix(ParquetDataPageVersion.V1, ParquetDataPageVersion.V2)] ParquetDataPageVersion version,
        [Matrix(EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked,
            EncodingKind.ByteStreamSplit)] EncodingKind encoding,
        [Matrix(false, true)] bool smallPages)
        => AssertRoundTrip(shape, version, encoding, smallPages, ParquetPhysicalType.Int64,
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true),
            CreateValues(static value => Epoch.AddMinutes(value)));

    [Test]
    [MatrixDataSource]
    public void MemoryValuesAreWrittenWithDefinitionLevels(
        [Matrix(Shape.OptionalLeaf, Shape.RequiredLeafInOptionalGroup)] Shape shape,
        [Matrix(ParquetDataPageVersion.V1, ParquetDataPageVersion.V2)] ParquetDataPageVersion version,
        [Matrix(EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaLengthByteArray)] EncodingKind encoding,
        [Matrix(false, true)] bool smallPages)
    {
        var options = new ColumnOptions(encodings: [encoding]);
        var schema = new ParquetSchema([
            shape == Shape.OptionalLeaf
                ? ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.ByteArray, options)
                : ColumnDefinition.OptionalGroup("group",
                    ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.ByteArray, options))
        ]);
        var expected = CreateValues(static value => System.Text.Encoding.UTF8.GetBytes($"value {value}"));
        var label = $"Memory {shape} {version} {encoding} smallPages={smallPages}";
        var file = Write<ReadOnlyMemory<byte>>(schema, version, smallPages, serialized =>
        {
            serialized.Serialize(expected.Select(static value => (ReadOnlyMemory<byte>)value).ToArray());
            if (serialized.Statistics.NullCount != 0 || !serialized.Statistics.HasStatistics)
                throw new InvalidOperationException($"{label}: column statistics report nulls.");
        });

        using (var reader = schema.CreateReader(new MemoryReadSource(file)))
        {
            var actual = new List<byte[]?>();
            foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
                for (var i = 0; i < buffer.Count; i++)
                    actual.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
            AssertBinaryValues(actual, expected, $"Plank {label}");
        }

        using var arrowReader = new ParquetSharp.ParquetFileReader(new MemoryStream(file), leaveOpen: false);
        using var rowGroup = arrowReader.RowGroup(0);
        using var column = rowGroup.Column(0);
        byte[]?[] arrowValues;
        if (shape == Shape.OptionalLeaf)
        {
            using var logical = column.LogicalReader<byte[]?>();
            arrowValues = logical.ReadAll(expected.Length);
        }
        else
        {
            using var logical = column.LogicalReader<ParquetSharp.Nested<byte[]>?>();
            arrowValues = logical.ReadAll(expected.Length).Select(static value => value?.Value).ToArray();
        }

        AssertBinaryValues(arrowValues, expected, $"Arrow {label}");
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void ConverterValuesAreWrittenWithDefinitionLevels(ParquetDataPageVersion version)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32,
                converter: new CustomMappedValueConverter())
        ]);
        CustomMappedValue[] expected = [new(7), new(11), new(7), new(-3)];
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = version
        });
        var serialized = writer.CreateSerializedColumn<CustomMappedValue>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<CustomMappedValue?>();
        foreach (var buffer in reader.RowGroups[0].Column<CustomMappedValue?>(0))
            actual.AddRange(buffer.Values);
        if (!actual.SequenceEqual(expected.Select(static value => (CustomMappedValue?)value)))
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    static T[] CreateValues<T>(Func<int, T> create)
    {
        // Few distinct values so the dictionary is kept, enough rows for several small pages.
        var values = new T[600];
        for (var i = 0; i < values.Length; i++)
            values[i] = create(i * 37 % 61 - 30);
        return values;
    }

    static void AssertRoundTrip<T>(Shape shape, ParquetDataPageVersion version, EncodingKind encoding,
        bool smallPages, ParquetPhysicalType physicalType, LogicalType? logicalType, T[] expected)
        where T : struct
    {
        var options = new ColumnOptions(encodings: [encoding]);
        var schema = new ParquetSchema([
            shape switch
            {
                Shape.OptionalLeaf => ColumnDefinition.OptionalLeaf("value", physicalType, options, logicalType),
                Shape.RequiredLeafInOptionalGroup => ColumnDefinition.OptionalGroup("group",
                    ColumnDefinition.RequiredLeaf("value", physicalType, options, logicalType)),
                _ => ColumnDefinition.OptionalGroup("group",
                    ColumnDefinition.OptionalLeaf("value", physicalType, options, logicalType))
            }
        ]);
        var leaf = schema.LeafColumns[0];
        var label = $"{typeof(T).Name} {shape} {version} {encoding} smallPages={smallPages}";

        var file = Write<T>(schema, version, smallPages, serialized =>
        {
            serialized.Serialize(expected);
            // The same values as all-present nullables on a flat optional leaf are the reference for the statistics.
            var nullable = new T?[expected.Length];
            for (var i = 0; i < expected.Length; i++)
                nullable[i] = expected[i];
            var referenceSchema = new ParquetSchema([
                ColumnDefinition.OptionalLeaf("value", physicalType, options, logicalType)
            ]);
            var reference = WriteNullableStatistics(referenceSchema, version, smallPages, nullable);
            AssertStatistics(serialized.Statistics, reference, label);
            var dataPages = 0;
            for (var i = 0; i < serialized.Pages.Count; i++)
            {
                ref var page = ref serialized.Pages[i];
                if (page.Kind == PageKind.Dictionary)
                    continue;
                dataPages++;
                if (page.NullCount != 0 || page.Statistics.NullCount != 0)
                    throw new InvalidOperationException($"{label}: data page {i} reports nulls.");
            }

            // Optional pages are sized by bytes only for PLAIN and dictionary indexes.
            if (smallPages && encoding is EncodingKind.Plain or EncodingKind.RleDictionary && dataPages < 2)
                throw new InvalidOperationException($"{label}: the small page budget wrote a single page.");
        });

        using (var reader = schema.CreateReader(new MemoryReadSource(file)))
        {
            // Every row is present, so every definition level is the leaf's maximum.
            var values = new List<T?>();
            var definitionLevels = new List<int>();
            foreach (var buffer in reader.RowGroups[0].NestedColumn<T>(leaf))
            {
                foreach (var value in buffer.Values.Values)
                    values.Add(value);
                definitionLevels.AddRange(buffer.DefinitionLevels);
            }
            AssertValues(values, expected, $"Plank {label}");
            if (definitionLevels.Count != expected.Length
                || definitionLevels.Any(level => level != leaf.MaxDefinitionLevel))
                throw new InvalidOperationException(
                    $"Plank {label}: definition levels are not all {leaf.MaxDefinitionLevel}.");

            if (shape != Shape.OptionalLeafInOptionalGroup)
            {
                var optionalValues = new List<T?>();
                foreach (var buffer in reader.RowGroups[0].Column<T?>(leaf))
                    optionalValues.AddRange(buffer.Values);
                AssertValues(optionalValues, expected, $"Plank optional {label}");
            }
        }

        using var arrowReader = new ParquetSharp.ParquetFileReader(new MemoryStream(file), leaveOpen: false);
        using var rowGroup = arrowReader.RowGroup(0);
        using var chunk = rowGroup.MetaData.GetColumnChunkMetaData(0);
        if (chunk.Statistics is not { } statistics || statistics.NullCount != 0)
            throw new InvalidOperationException($"Arrow {label}: column chunk does not report zero nulls.");
        using var column = rowGroup.Column(0);
        T?[] arrowValues;
        switch (shape)
        {
            case Shape.OptionalLeaf:
            {
                using var logical = column.LogicalReader<T?>();
                arrowValues = logical.ReadAll(expected.Length);
                break;
            }
            case Shape.RequiredLeafInOptionalGroup:
            {
                using var logical = column.LogicalReader<ParquetSharp.Nested<T>?>();
                arrowValues = logical.ReadAll(expected.Length).Select(static value => value?.Value).ToArray();
                break;
            }
            default:
            {
                using var logical = column.LogicalReader<ParquetSharp.Nested<T?>?>();
                arrowValues = logical.ReadAll(expected.Length).Select(static value => value?.Value).ToArray();
                break;
            }
        }

        AssertValues(arrowValues, expected, $"Arrow {label}");
    }

    static byte[] Write<T>(ParquetSchema schema, ParquetDataPageVersion version, bool smallPages,
        Action<SerializedColumn<T>> serialize)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = version,
            WritePageIndexes = smallPages,
            TargetDataPageSizeBytes = smallPages ? 256u : 1024 * 1024
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialize(serialized);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    static ColumnStatistics WriteNullableStatistics<T>(ParquetSchema schema, ParquetDataPageVersion version,
        bool smallPages, T?[] values)
        where T : struct
    {
        ColumnStatistics statistics = default;
        _ = Write<T?>(schema, version, smallPages, serialized =>
        {
            serialized.Serialize(values);
            statistics = serialized.Statistics;
        });
        return statistics;
    }

    static void AssertStatistics(ColumnStatistics actual, ColumnStatistics expected, string label)
    {
        if (actual.ValueKind != expected.ValueKind || actual.MinBits != expected.MinBits
            || actual.MaxBits != expected.MaxBits || actual.NullCount != 0 || expected.NullCount != 0
            || actual.NanCount != expected.NanCount || actual.HasStatistics != expected.HasStatistics
            || !actual.HasStatistics)
            throw new InvalidOperationException($"{label}: column statistics differ from all-present nullables.");
    }

    static void AssertBinaryValues(IReadOnlyList<byte[]?> actual, byte[][] expected, string label)
    {
        if (actual.Count != expected.Length)
            throw new InvalidOperationException($"{label}: expected {expected.Length} values, got {actual.Count}.");
        for (var i = 0; i < expected.Length; i++)
            if (actual[i] is not { } value || !value.AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"{label}: value {i} changed.");
    }

    static void AssertValues<T>(IReadOnlyList<T?> actual, T[] expected, string label)
        where T : struct
    {
        if (actual.Count != expected.Length)
            throw new InvalidOperationException($"{label}: expected {expected.Length} values, got {actual.Count}.");
        for (var i = 0; i < expected.Length; i++)
            if (!actual[i].Equals(expected[i]))
                throw new InvalidOperationException(
                    $"{label}: value {i} expected {expected[i]}, got {actual[i]?.ToString() ?? "null"}.");
    }
}
