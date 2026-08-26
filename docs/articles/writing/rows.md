# Row write layer

The row write layer ingests rows and automatically creates row groups around the target size.

Use this layer when you want to produce good Parquet files without managing their structure yourself.

By default, row groups target [128 MiB](https://iceberg.apache.org/docs/latest/configuration/#write-properties).

It can also roll over to a new file around the [512 MiB](https://iceberg.apache.org/docs/latest/configuration/#write-properties) default target with `EventSchema.CreateRowWriter(filePath, file)`.

## Write rows

```csharp
using var stream = File.Create("events.parquet");
using var writer = EventSchema.CreateRowWriter(stream);

for (var id = 0; id < 100; id++)
{
    var row = writer.GetRow();
    row.Id = id;
    row.Name = "event"u8.ToArray();
    row.OccurredAt = DateTimeOffset.UtcNow;
    writer.Next();
}

writer.Complete();
```

`GetRow()` returns the reusable row buffer. Call `Next()` after filling it, then `Complete()` after the last row.

The row writer handles row-group construction, encoding, and file finalization. `Complete()` commits the file. If
writing fails first, `Dispose()` stops the workers and releases resources without committing the incomplete file.
A disposed writer cannot be reused.
