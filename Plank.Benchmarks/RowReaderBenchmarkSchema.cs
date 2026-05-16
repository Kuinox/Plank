using Plank.Schema;

namespace Plank.Benchmarks;

[ParquetSchema]
public sealed partial class RowReaderBenchmarkSchema
{
    [ParquetColumn("id")]
    public int Id { get; set; }

    [ParquetColumn("timestamp")]
    public long Timestamp { get; set; }

    [ParquetColumn("value")]
    public double Value { get; set; }

    [ParquetColumn("category")]
    public int Category { get; set; }

    [ParquetColumn("label")]
    public string Label { get; set; } = "";
}
