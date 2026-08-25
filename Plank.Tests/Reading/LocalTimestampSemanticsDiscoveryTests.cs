using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class LocalTimestampSemanticsDiscoveryTests
{
    [Test]
    public void LocalTimestampRoundTripPreservesWallClockAndUnspecifiedKind()
    {
        DateTime[] expected =
        [
            new DateTime(1969, 12, 31, 23, 59, 58, DateTimeKind.Unspecified).AddTicks(123_450),
            new DateTime(2026, 7, 29, 1, 2, 3, DateTimeKind.Unspecified).AddTicks(456_780)
        ];
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf(
                "value",
                ParquetPhysicalType.Int64,
                logicalType: new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: false))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);

        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryStream(stream.ToArray()));
        var actual = new List<DateTime>();
        foreach (var buffer in reader.RowGroups[0].Column<DateTime>(0))
            actual.AddRange(buffer.Values);

        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Expected local wall-clock values [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        if (actual.Any(static value => value.Kind != DateTimeKind.Unspecified))
            throw new InvalidOperationException("Local timestamps were materialized with an adjusted DateTime kind.");
    }

    [Test]
    public void LocalTimestampRejectsDateTimeOffsetSerialization()
    {
        var value = new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.FromHours(2));

        AssertSerializationRejected(
            CreateLocalTimestampSchema(optional: false),
            new[] { value });
        AssertSerializationRejected(
            CreateLocalTimestampSchema(optional: true),
            new DateTimeOffset?[] { value, null });
    }

    [Test]
    public void LocalTimestampRejectsDateTimeOffsetProjection()
    {
        var value = new DateTime(2026, 7, 29, 1, 2, 3, DateTimeKind.Unspecified);
        var requiredSchema = CreateLocalTimestampSchema(optional: false);
        var requiredFile = WriteFile(requiredSchema, new[] { value });
        AssertProjectionRejected<DateTimeOffset>(requiredSchema, requiredFile);

        var optionalSchema = CreateLocalTimestampSchema(optional: true);
        var optionalFile = WriteFile(optionalSchema, new DateTime?[] { value, null });
        AssertProjectionRejected<DateTimeOffset?>(optionalSchema, optionalFile);
    }

    static ParquetSchema CreateLocalTimestampSchema(bool optional)
    {
        var logicalType = new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: false);
        return new([
            optional
                ? ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int64, logicalType: logicalType)
                : ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64, logicalType: logicalType)
        ]);
    }

    static void AssertSerializationRejected<T>(ParquetSchema schema, T[] values)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);

        Assert.Throws<InvalidOperationException>(() => serialized.Serialize(values));
    }

    static void AssertProjectionRejected<T>(ParquetSchema schema, byte[] file)
    {
        using var reader = schema.CreateReader(new MemoryStream(file));
        Assert.Throws<NotSupportedException>(() =>
        {
            foreach (var buffer in reader.RowGroups[0].Column<T>(0))
                _ = buffer.Values.Length;
        });
    }

    static byte[] WriteFile<T>(ParquetSchema schema, T[] values)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }
}
