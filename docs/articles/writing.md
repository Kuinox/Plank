# Writing parquet files

Plank exposes two main write styles:

- a source-generated row API for ergonomic record-like writes
- a column API for explicit control over serialized columns and row groups

## Row API

Use the row API when your data naturally arrives as rows. A `[ParquetSchema]` model produces a generated row writer.

```csharp
using var stream = File.Create("events.parquet");
var rowWriter = EventSchema.CreateRowWriter(stream);

foreach (var item in events)
{
    var row = rowWriter.GetRow();
    row.Id = item.Id;
    row.Name = item.Name;
    row.OccurredAt = item.OccurredAt;
    rowWriter.Next();
}

rowWriter.Complete();
```

`Complete()` finalizes pending row groups and closes the parquet file metadata.

## Column API

Use the column API when each column is already available as a contiguous buffer or can be prepared independently.

```csharp
using var stream = File.Create("events.parquet");
var writer = EventSchema.CreateWriter(stream);
var rowGroup = writer.StartRowGroup();

var ids = rowGroup.Id;
ids.Serialize([1, 2, 3]);
rowGroup.Write(ids);

var names = rowGroup.Name;
names.Serialize(["created", "updated", "deleted"]);
rowGroup.Write(names);

writer.CloseFile();
```

Columns can also be serialized ahead of row-group writing and written later. That keeps expensive encoding work separate from file assembly.

## Writer options

`ParquetWriterOptions` controls writer behavior:

- `Compression` selects the file compression codec.
- `BufferPool` provides reusable arrays for writer scratch memory.
- `TargetDataPageSizeBytes`, `InitialPageBufferBytes`, and `InitialPageCapacity` tune page buffering.
- `RowApiMaxParallelism` controls row API worker concurrency.

```csharp
using Plank.Writing;

var options = new ParquetWriterOptions
{
    Compression = CompressionKind.Snappy,
    TargetDataPageSizeBytes = 64 * 1024
};

using var stream = File.Create("events.parquet");
var writer = EventSchema.CreateWriter(stream, options);
```
