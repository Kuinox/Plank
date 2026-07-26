using Plank.Schema;

namespace Plank.Sample;

[ParquetSchema]
public sealed partial class EventSchema
{
    public int Id { get; init; }

    public byte[] Name { get; init; } = [];

    public DateTimeOffset OccurredAt { get; init; }
}
