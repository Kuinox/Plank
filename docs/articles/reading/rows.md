# Row read layer

The row read layer exposes parquet data as strongly typed rows. It is built on top of the logical reader.

Use it when you want to ingest data as rows instead of columns.

The examples use the [`EventSchema`](../schema.md#define-a-schema) type declared in [Schema](../schema.md). The same declaration is also used by the logical read layer and the writing APIs.

## Read rows

Call [`CreateRowReader`](../schema.md#define-a-schema) and enumerate the reader:

```csharp
using var stream = File.OpenRead("events.parquet");
using EventSchema.RowReader reader = EventSchema.CreateRowReader(stream);

foreach (EventSchema.Row row in reader)
    Console.WriteLine($"{row.Id}: {row.Name} at {row.OccurredAt}");
```

The reader binds properties to file columns by name, so the columns do not need to appear in the same order in the file.

You can also use `MoveNext` and `Current` when explicit iteration is more convenient:

```csharp
while (reader.MoveNext())
{
    EventSchema.Row row = reader.Current;
    Console.WriteLine(row.Id);
}
```

> [!NOTE]
> A row is a temporary view over the reader's current buffers. Read its properties before advancing the reader.
> Binary properties return a scoped value whose bytes can be retained when they must outlive the current iteration.

For example, a schema property named `Payload` exposes its current bytes through `Span`, along with
its null state and a `Retain()` operation. Retain the current value only when ownership is required:

```csharp
while (reader.MoveNext())
{
    EventSchema.ReadRow row = reader.Current;
    RowReaderBinaryValue value = row.Payload;
    if (value.IsNull)
        continue;

    ConsumeNow(value.Span);
    using ParquetBuffer payload = value.Retain();
    ConsumeLater(payload.Span);
}
```

The retained buffer is an exact slice of the current value and remains valid after the reader
advances. Dispose it when it is no longer needed.

## Read selected properties

Pass a projection to decode only the properties you need:

```csharp
EventSchema.Projection projection = EventSchema.Projection.Id |
    EventSchema.Projection.Name;

using var stream = File.OpenRead("events.parquet");
using EventSchema.RowReader reader = EventSchema.CreateRowReader(stream, projection);

foreach (EventSchema.Row row in reader)
    Console.WriteLine($"{row.Id}: {row.Name}");
```

Every property has a matching projection. Combine projections with `|`, or use `Projection.All` to select every property. Accessing a property that was not selected throws `InvalidOperationException`.

## Schema compatibility

By default, the row reader requires each selected column to exist and match the row schema. It validates physical type, logical type, and required or optional repetition.

For files whose schema evolves over time, pass [`ParquetSchemaEvolutionOptions`](xref:Plank.Reading.ParquetSchemaEvolutionOptions) to [`CreateRowReader`](../schema.md#define-a-schema). The options can allow selected compatibility changes, such as materializing a default value for a missing column or reading a required file column into a nullable property.
