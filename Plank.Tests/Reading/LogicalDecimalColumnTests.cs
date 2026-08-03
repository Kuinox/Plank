using System.Collections.Immutable;
using ParquetSharp;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class LogicalDecimalColumnTests
{
    [Test]
    public void DecimalValuesRoundTripAcrossPhysicalCarriersAndEncodings()
    {
        decimal[] expected = [-9_999_999.99m, -12.34m, 0m, 12.34m, 9_999_999.99m, 12.34m];
        var cases = new[]
        {
            (ParquetPhysicalType.Int32, 0u, EncodingKind.Plain),
            (ParquetPhysicalType.Int64, 0u, EncodingKind.DeltaBinaryPacked),
            (ParquetPhysicalType.FixedLenByteArray, 4u, EncodingKind.ByteStreamSplit),
            (ParquetPhysicalType.ByteArray, 0u, EncodingKind.RleDictionary)
        };

        foreach (var (physicalType, typeLength, encoding) in cases)
        {
            var schema = CreateSchema(physicalType, 9, 2, typeLength, encoding);
            var actual = RoundTrip(schema, expected);
            AssertSequenceEqual(expected, actual, $"{physicalType}/{encoding}");
        }
    }

    [Test]
    public void NullableDecimalValuesRoundTrip()
    {
        decimal?[] expected =
        [
            -12_345_678_901_234_567_890.12345678m,
            null,
            0m,
            12_345_678_901_234_567_890.12345678m
        ];
        var schema = CreateSchema(ParquetPhysicalType.FixedLenByteArray, 29, 8, 13,
            EncodingKind.ByteStreamSplit, optional: true);

        AssertSequenceEqual(expected, RoundTrip(schema, expected), "nullable fixed decimal");
    }

    [Test]
    public void DecimalSerializationRejectsPrecisionLossAndOverflow()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32, 5, 2, 0, EncodingKind.Plain);
        AssertSerializationRejected(schema, 1.234m, typeof(InvalidOperationException));
        AssertSerializationRejected(schema, 1_000m, typeof(OverflowException));
    }

    [Test]
    public void FixedDecimalFileIsReadableByParquetSharp()
    {
        decimal[] expected =
        [
            -12_345_678_901_234_567_890.123456m,
            0m,
            12_345_678_901_234_567_890.123456m
        ];
        var schema = CreateSchema(ParquetPhysicalType.FixedLenByteArray, 29, 6, 13, EncodingKind.Plain);
        var path = Path.Combine(Path.GetTempPath(), $"plank-decimal-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var stream = File.Create(path))
                Write(schema, stream, expected);

            using var reader = new ParquetFileReader(path);
            using var rowGroup = reader.RowGroup(0);
            var actual = rowGroup.Column(0).LogicalReader<decimal>().ReadAll(expected.Length);
            AssertSequenceEqual(expected, actual, "ParquetSharp");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType, int precision, int scale,
        uint typeLength, EncodingKind encoding, bool optional = false)
    {
        var options = new ColumnOptions(encodings: ImmutableArray.Create(encoding), typeLength: typeLength);
        var pageStrategy = encoding == EncodingKind.RleDictionary
            ? ForceDictionaryPageStrategy.Shared
            : null;
        var logicalType = new Plank.Schema.LogicalType.Decimal(precision, scale);
        var definition = optional
            ? ColumnDefinition.OptionalLeaf("value", physicalType, options, logicalType, pageStrategy)
            : ColumnDefinition.RequiredLeaf("value", physicalType, options, logicalType, pageStrategy);
        return new([definition]);
    }

    static T[] RoundTrip<T>(ParquetSchema schema, T[] values)
    {
        using var stream = new MemoryStream();
        Write(schema, stream, values);

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<T>();
        foreach (var buffer in reader.RowGroups[0].Column<T>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static void Write<T>(ParquetSchema schema, Stream stream, T[] values)
    {
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
    }

    static void AssertSerializationRejected(ParquetSchema schema, decimal value, Type exceptionType)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
        var serialized = writer.CreateSerializedColumn<decimal>(schema.LeafColumns[0]);
        var exception = Assert.Throws<Exception>(() => serialized.Serialize([value]));
        if (exception.GetType() != exceptionType)
            throw new InvalidOperationException(
                $"Expected {exceptionType.Name}, got {exception.GetType().Name}.", exception);
    }

    static void AssertSequenceEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual, string label)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"{label}: expected [{string.Join(", ", expected.ToArray())}], got [{string.Join(", ", actual.ToArray())}].");
    }
}
