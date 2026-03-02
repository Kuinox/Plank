using Plank.Schema;

namespace Plank.Sample;

[ParquetSchema]
public sealed partial class EventSchema
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }
}
