using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class SchemaRuntimeMismatchDiscoveryTests
{
    [Test]
    public void TimeOnlySupportsTheRequiredInt32PhysicalTypeForTimeMillis()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain]),
                logicalType: new LogicalType.Time(TimeUnit.Millis, IsAdjustedToUtc: false))
        ]);
        TimeOnly[] expected =
        [
            TimeOnly.MinValue,
            new(12, 34, 56, 789),
            new(23, 59, 59, 999)
        ];

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<TimeOnly>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<TimeOnly>();
        foreach (var buffer in reader.RowGroups[0].Column<TimeOnly>(0))
            foreach (var value in buffer.Values)
                actual.Add(value);
        if (!actual.ToArray().AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    [Test]
    public void MapRejectsMismatchedKeyAndValueCardinalityPerRow()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Map("scores",
                ColumnDefinition.RequiredLeaf("key", ParquetPhysicalType.Int32),
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32))
        ]);

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();
        var keys = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
        var values = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[1]);

        Assert.Throws<InvalidOperationException>(() =>
        {
            keys.Serialize([[1, 2], [3]]);
            rowGroup.Write(keys);
            values.Serialize([[10], [20, 30]]);
            rowGroup.Write(values);
            writer.CloseFile();
        });
    }

    [Test]
    public void MapRejectsMismatchedCardinalityInEveryValueLeaf()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Map("scores",
                ColumnDefinition.RequiredLeaf("key", ParquetPhysicalType.Int32),
                ColumnDefinition.RequiredGroup("value",
                    ColumnDefinition.RequiredLeaf("first", ParquetPhysicalType.Int32),
                    ColumnDefinition.RequiredLeaf("second", ParquetPhysicalType.Int32)))
        ]);

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var rowGroup = writer.StartRowGroup();
        var keys = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
        var firstValues = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[1]);
        var secondValues = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[2]);

        keys.Serialize([[1, 2], [3]]);
        rowGroup.Write(keys);
        firstValues.Serialize([[10, 20], [30]]);
        rowGroup.Write(firstValues);
        secondValues.Serialize([[100], [200, 300]]);

        Assert.Throws<InvalidOperationException>(() => rowGroup.Write(secondValues));
    }

    [Test]
    public void MapCardinalityIgnoresValueInternalListLengths()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Map("scores",
                ColumnDefinition.RequiredLeaf("key", ParquetPhysicalType.Int32),
                ColumnDefinition.List("value",
                    ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32)))
        ]);

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var rowGroup = writer.StartRowGroup();
        var keys = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
        var values = rowGroup.CreateSerializedColumn<int[][]>(schema.LeafColumns[1]);

        keys.Serialize([[1, 2], [3]]);
        rowGroup.Write(keys);
        values.Serialize([[[10, 11], [20]], [[30, 31, 32]]]);
        rowGroup.Write(values);
        writer.CloseFile();
    }
}
