using Plank.Schema;

namespace Plank.Sample;

#region DecimalSchema
[ParquetSchema]
public sealed partial class InvoiceSchema
{
    [ParquetColumn(Precision = 10, Scale = 2)]
    public decimal? Amount { get; init; }
}
#endregion

static class DecimalApiSample
{
    public static void Run()
    {
        using var stream = new MemoryStream();
        using (var writer = InvoiceSchema.CreateRowWriter(stream))
        {
            writer.GetRow().Amount = 12.34m;
            writer.GetRow().Amount = null;
            writer.Complete();
        }
        using var input = new MemoryStream(stream.ToArray());
        using var reader = InvoiceSchema.CreateRowReader(input);
        if (!reader.MoveNext() || reader.Current.Amount != 12.34m ||
            !reader.MoveNext() || reader.Current.Amount is not null || reader.MoveNext())
            throw new InvalidOperationException("The decimal sample did not roundtrip scaled and null amounts.");
    }
}
