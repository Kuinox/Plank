using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class GeneratedNestedRowSchema
{
    public int Sequence { get; set; }

    public Guid CorrelationId { get; set; }

    public string Label { get; set; } = string.Empty;

    public int?[][]? Values { get; set; }

    public Dictionary<int, int?>? Scores { get; set; }

    public GeneratedNestedAddress? Location { get; set; }

    public List<GeneratedNestedEntry>? Items { get; set; }

    public List<string?>? Names { get; set; }

    public List<Guid?>? Identifiers { get; set; }

    public List<DateOnly?>? Dates { get; set; }

    public List<TimeOnly?>? Times { get; set; }

    public List<DateTime?>? Timestamps { get; set; }

    public List<DateTimeOffset?>? Instants { get; set; }
}
