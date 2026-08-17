using Plank.Schema;
using Plank.Writing;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

/// <summary>
/// The plain BYTE_ARRAY page writer decides the column's min and max while it copies, so these pin
/// the answers it has to keep producing: across page boundaries, for both row shapes, and only where
/// the column really does order its bytes lexicographically.
/// </summary>
internal sealed class PlainByteArrayStatisticsTests
{
    [Test]
    public void MinAndMaxSpanEveryPageOfTheColumn()
    {
        // 16 bytes a row against a 32-byte target puts two rows on a page, so the min sits on the
        // last page and the max on the first. A per-page accumulator that forgot the previous page
        // would report one of them wrong.
        var column = CreateByteArrayColumn(32);

        column.Serialize([
            "mmmmmmmmmmmm"u8.ToArray(),
            "zzzzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqqqq"u8.ToArray(),
            "rrrrrrrrrrrr"u8.ToArray(),
            "aaaaaaaaaaaa"u8.ToArray()
        ]);

        AssertMinMax(column.Statistics, "aaaaaaaaaaaa"u8, "zzzzzzzzzzzz"u8);
    }

    [Test]
    public void PrefixesCompareByLengthLikeASequenceComparison()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize(["ab"u8.ToArray(), "abc"u8.ToArray(), "a"u8.ToArray(), "abcd"u8.ToArray()]);

        AssertMinMax(column.Statistics, "a"u8, "abcd"u8);
    }

    [Test]
    public void AnEmptyValueIsTheSmallestValue()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize(["b"u8.ToArray(), [], "a"u8.ToArray()]);

        AssertMinMax(column.Statistics, ""u8, "b"u8);
    }

    [Test]
    public void RepeatedValuesReportTheSameMinAndMax()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize(["same"u8.ToArray(), "same"u8.ToArray(), "same"u8.ToArray()]);

        AssertMinMax(column.Statistics, "same"u8, "same"u8);
        if (column.Statistics.DistinctCount != 1)
            throw new InvalidOperationException(
                $"Expected a single distinct value, got {column.Statistics.DistinctCount}.");
    }

    [Test]
    public void HighBytesCompareAsUnsigned()
    {
        var column = CreateByteArrayColumn(1024);

        column.Serialize([new byte[] { 0x7f }, new byte[] { 0xff }, new byte[] { 0x80 }, new byte[] { 0x01 }]);

        AssertMinMax(column.Statistics, [0x01], [0xff]);
    }

    [Test]
    public void MemoryRowShapeReportsTheSameMinAndMax()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 32
        });
        var column = writer.CreateSerializedColumn<ReadOnlyMemory<byte>>(schema.LeafColumns[0]);

        column.Serialize([
            "mmmmmmmmmmmm"u8.ToArray(),
            "zzzzzzzzzzzz"u8.ToArray(),
            "qqqqqqqqqqqq"u8.ToArray(),
            "rrrrrrrrrrrr"u8.ToArray(),
            "aaaaaaaaaaaa"u8.ToArray()
        ]);

        AssertMinMax(column.Statistics, "aaaaaaaaaaaa"u8, "zzzzzzzzzzzz"u8);
    }

    [Test]
    public void DecimalColumnsKeepTheirSignedOrdering()
    {
        // Two's complement bytes: 0xFF is -1 and 0x01 is 1, so the decimal order is the reverse of
        // the lexicographic one the page writer tracks. This column has to fall back to the
        // statistics pass that knows the difference.
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("amount", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.Decimal(9, 2))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 1024
        });
        var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);

        column.Serialize([new byte[] { 0x01 }, new byte[] { 0xff }, new byte[] { 0x7f }]);

        AssertMinMax(column.Statistics, [0xff], [0x7f]);
    }

    static SerializedColumn<byte[]> CreateByteArrayColumn(uint targetDataPageSizeBytes)
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray)
        ]);
        var writer = schema.CreateWriter(new MemoryStream(), new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = targetDataPageSizeBytes
        });
        return writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
    }

    static void AssertMinMax(ColumnStatistics statistics, ReadOnlySpan<byte> min, ReadOnlySpan<byte> max)
    {
        if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.Binary)
            throw new InvalidOperationException($"Expected binary statistics, got {statistics.ValueKind}.");
        if (!statistics.GetMinValue().SequenceEqual(min))
            throw new InvalidOperationException(
                $"Min mismatch. Expected '{Describe(min)}', got '{Describe(statistics.GetMinValue())}'.");
        if (!statistics.GetMaxValue().SequenceEqual(max))
            throw new InvalidOperationException(
                $"Max mismatch. Expected '{Describe(max)}', got '{Describe(statistics.GetMaxValue())}'.");
    }

    static string Describe(ReadOnlySpan<byte> value) => Convert.ToHexString(value);
}
