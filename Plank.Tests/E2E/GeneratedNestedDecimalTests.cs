using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNestedDecimalTests
{
    [Test]
    public void DecimalColumnsRoundTripAllCarriersAlongsideAList()
    {
        decimal[] small = [-9_999_999.99m, 0m, 9_999_999.99m];
        decimal[] large = [-999_999_999_999_999.999m, 0m, 999_999_999_999_999.999m];
        decimal[] wide = [-7_922_816_251_426_433_759_354_395.0335m, 0m, 7_922_816_251_426_433_759_354_395.0335m];
        using var stream = new MemoryStream();
        using (var writer = NestedDecimalRow.CreateRowWriter(stream,
                   new ParquetWriterOptions { Compression = CompressionKind.None }))
        {
            for (var i = 0; i < small.Length; i++)
            {
                var row = writer.GetRow();
                row.Fine = i == 1 ? 0m : i == 0 ? -0.0000000000000000000000000001m : 0.0000000000000000000000000001m;
                row.Small32 = small[i];
                row.Small64 = large[i];
                row.Fixed = wide[i];
                row.Variable = wide[i];
                row.Optional32 = i == 1 ? null : small[i];
                row.Optional64 = i == 1 ? null : large[i];
                row.OptionalFixed = i == 1 ? null : wide[i];
                row.OptionalVariable = i == 1 ? null : wide[i];
                row.Values = i == 1 ? [] : [i, i + 1];
                row.Details = i == 1 ? null : new NestedDecimalDetails { Amount = wide[i] };
            }
            writer.Complete();
        }

        using var input = new MemoryStream(stream.ToArray());
        using var reader = NestedDecimalRow.CreateRowReader(input);
        var index = 0;
        foreach (var row in reader)
        {
            if (row.Fine != (index == 1 ? 0m : index == 0 ? -0.0000000000000000000000000001m : 0.0000000000000000000000000001m))
                throw new InvalidOperationException("Maximum-scale decimal changed during round-trip.");
            if (row.Small32 != small[index] || row.Small64 != large[index] ||
                row.Fixed != wide[index] || row.Variable != wide[index])
                throw new InvalidOperationException("Required decimal changed during round-trip.");
            if (row.Optional32 != (index == 1 ? null : (decimal?)small[index]) ||
                row.Optional64 != (index == 1 ? null : (decimal?)large[index]) ||
                row.OptionalFixed != (index == 1 ? null : (decimal?)wide[index]) ||
                row.OptionalVariable != (index == 1 ? null : (decimal?)wide[index]))
                throw new InvalidOperationException("Optional decimal value or null changed during round-trip.");
            if (row.Details?.Amount != (index == 1 ? null : (decimal?)wide[index]))
                throw new InvalidOperationException("Decimal-only optional group presence or value changed.");
            if (!row.Values.SequenceEqual(index == 1 ? [] : new[] { index, index + 1 }))
                throw new InvalidOperationException("Adjacent collection changed during round-trip.");
            index++;
        }
        if (index != small.Length)
            throw new InvalidOperationException("Unexpected decimal row count.");

        var columns = NestedDecimalRow.Schema.LeafColumns;
        if (columns[2].Options.TypeLength != 13 || columns[3].Options.TypeLength != 0 ||
            columns[2].LogicalType is not LogicalType.Decimal { Precision: 29, Scale: 4 })
            throw new InvalidOperationException("Decimal precision, scale or fixed width was not preserved.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void DecimalRuntimeBoundsRemainEnforced(bool exceedsPrecision)
    {
        using var stream = new MemoryStream();
        using var writer = NestedDecimalRow.CreateRowWriter(stream,
            new ParquetWriterOptions { Compression = CompressionKind.None });
        var row = writer.GetRow();
        row.Small32 = exceedsPrecision ? 10_000_000m : 0.001m;
        row.Values = [];
        try
        {
            writer.Complete();
        }
        catch (OverflowException) when (exceedsPrecision)
        {
            return;
        }
        catch (InvalidOperationException exception) when (!exceedsPrecision &&
            exception.Message.Contains("scale", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException("Out-of-range decimal was accepted by the generated nested writer.");
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class NestedDecimalRow
{
    [ParquetColumn(ParquetPhysicalType.Int32, Precision = 9, Scale = 2)]
    public decimal Small32 { get; set; }
    [ParquetColumn(ParquetPhysicalType.Int64, Precision = 18, Scale = 3)]
    public decimal Small64 { get; set; }
    [ParquetColumn(Precision = 29, Scale = 4)]
    public decimal Fixed { get; set; }
    [ParquetColumn(ParquetPhysicalType.ByteArray, Precision = 29, Scale = 4)]
    public decimal Variable { get; set; }
    [ParquetColumn(ParquetPhysicalType.Int32, Precision = 9, Scale = 2)]
    public decimal? Optional32 { get; set; }
    [ParquetColumn(ParquetPhysicalType.Int64, Precision = 18, Scale = 3)]
    public decimal? Optional64 { get; set; }
    [ParquetColumn(Precision = 29, Scale = 4)]
    public decimal? OptionalFixed { get; set; }
    [ParquetColumn(ParquetPhysicalType.ByteArray, Precision = 29, Scale = 4)]
    public decimal? OptionalVariable { get; set; }
    [ParquetColumn(Precision = 29, Scale = 28)]
    public decimal Fine { get; set; }
    public int[] Values { get; set; } = [];
    public NestedDecimalDetails? Details { get; set; }
}

internal sealed class NestedDecimalDetails
{
    [ParquetColumn(Precision = 29, Scale = 4)]
    public decimal Amount { get; set; }
}
