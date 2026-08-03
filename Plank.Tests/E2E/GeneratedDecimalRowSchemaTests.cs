using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class GeneratedDecimalRowSchemaTests
{
    [Test]
    public void GeneratedDecimalSchemaCarriesPrecisionScaleAndFixedWidth()
    {
        var amount = DecimalRowSchema.Schema.LeafColumns[0];
        var optional = DecimalRowSchema.Schema.LeafColumns[1];

        AssertDecimal(amount, precision: 18, scale: 4, typeLength: 8);
        AssertDecimal(optional, precision: 29, scale: 8, typeLength: 13);
    }

    [Test]
    public void GeneratedDecimalColumnsRoundTrip()
    {
        decimal[] expected = [-123_456_789_012.3456m, 0m, 123_456_789_012.3456m];
        decimal?[] expectedOptional = [-12_345_678_901_234_567_890.12345678m, null,
            12_345_678_901_234_567_890.12345678m];
        using var stream = new MemoryStream();
        var writer = DecimalRowSchema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();
        rowGroup.Amount.Serialize(expected);
        rowGroup.Write(rowGroup.Amount);
        rowGroup.OptionalAmount.Serialize(expectedOptional);
        rowGroup.Write(rowGroup.OptionalAmount);
        writer.CloseFile();

        using var reader = DecimalRowSchema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<decimal>();
        var actualOptional = new List<decimal?>();
        foreach (var buffer in reader.RowGroups[0].AmountColumn)
            actual.AddRange(buffer.Values);
        foreach (var buffer in reader.RowGroups[0].OptionalAmountColumn)
            actualOptional.AddRange(buffer.Values);

        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("Generated required decimal values did not round-trip.");
        if (!actualOptional.SequenceEqual(expectedOptional))
            throw new InvalidOperationException("Generated optional decimal values did not round-trip.");
    }

    static void AssertDecimal(LeafColumn column, int precision, int scale, uint typeLength)
    {
        if (column.PhysicalType != ParquetPhysicalType.FixedLenByteArray)
            throw new InvalidOperationException($"Expected fixed-length decimal, got {column.PhysicalType}.");
        if (column.LogicalType is not LogicalType.Decimal decimalType ||
            decimalType.Precision != precision || decimalType.Scale != scale)
            throw new InvalidOperationException("Generated decimal logical type did not match the attribute.");
        if (column.Options.TypeLength != typeLength)
            throw new InvalidOperationException(
                $"Expected fixed length {typeLength}, got {column.Options.TypeLength}.");
    }
}
