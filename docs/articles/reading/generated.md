# Generated row reader

The generated row reader is the highest-level reader layer. It is produced from a `[ParquetSchema]` model and exposes rows through generated, strongly typed APIs.

Use it when application code wants row-shaped access instead of page-shaped access.

```csharp
using Plank.Schema;

[ParquetSchema]
public sealed partial class EventSchema
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }
}
```

Read all projected columns:

```csharp
using var stream = File.OpenRead("events.parquet");
using var reader = EventSchema.CreateRowReader(stream);

while (reader.MoveNext())
{
    var row = reader.Current;
    var id = row.Id;
    var name = row.Name;
    var occurredAt = row.OccurredAt;
}
```

The generator also emits a projection enum so callers can skip columns they do not need.

```csharp
using var stream = File.OpenRead("events.parquet");
using var reader = EventSchema.CreateRowReader(
    stream,
    EventSchema.Projection.Id | EventSchema.Projection.Name);

while (reader.MoveNext())
{
    var row = reader.Current;
    var id = row.Id;
    var name = row.Name;
}
```

This layer builds on the typed column reader. It is the right default for normal application reads when a source-generated schema model exists.
