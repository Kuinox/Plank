using System.Globalization;
using Plank.Schema;

namespace Plank.Sample;

[ParquetSchema(AllowAllocatingValues = true)]
public sealed partial class ImportExampleSchema
{
    public int? Count { get; init; }
    [ParquetColumn(Precision = 10, Scale = 2)]
    public decimal? Amount { get; init; }
    public DateTime When { get; init; }
    public string? Label { get; init; }
}

static class UnknownSchemaSample
{
    public static void Run()
    {
        using var stream = new MemoryStream();
        using (var writer = ImportExampleSchema.CreateRowWriter(stream))
        {
            var first = writer.GetRow();
            first.Count = 7;
            first.Amount = 12.34m;
            first.When = DateTime.UnixEpoch;
            first.Label = "hello";
            var second = writer.GetRow();
            second.Count = null;
            second.Amount = null;
            second.When = DateTime.UnixEpoch;
            second.Label = null;
            writer.Complete();
        }
        using var input = new MemoryStream(stream.ToArray());
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        UnknownSchemaReader.Dump(input, output);
        var expected = string.Join(Environment.NewLine, new[]
        {
            "Column: Count", "7", "<null>",
            "Column: Amount", "12.34", "<null>",
            "Column: When", "1970-01-01T00:00:00.0000000Z", "1970-01-01T00:00:00.0000000Z",
            "Column: Label", "hello", "<null>", ""
        });
        if (output.ToString() != expected)
            throw new InvalidOperationException($"Unexpected unknown-schema output: {output}");
    }
}
