# Row write layer

The row write layer ingests rows and automatically creates row groups around the target size.

Use this layer when you want to produce good Parquet files without managing their structure yourself.

By default, row groups target [128 MiB](https://iceberg.apache.org/docs/latest/configuration/#write-properties).

It can also roll over to a new file around the [512 MiB](https://iceberg.apache.org/docs/latest/configuration/#write-properties) default target with `EventSchema.CreateRowWriter(filePath, file)`.

## Write rows

```csharp
using var stream = File.Create("events.parquet");
using var writer = EventSchema.CreateRowWriter(stream);
EventSchema.RowCache cache = default;

for (var id = 0; id < 100; id++)
{
    var row = writer.GetRow(ref cache);
    row.Id = id;
    row.Name = "event"u8.ToArray();
    row.OccurredAt = DateTimeOffset.UtcNow;
}

writer.Complete();
```

`GetRow(ref cache)` commits the previously returned row and returns the next reusable row buffer. The stack-owned
cache keeps managed references to the current column arrays and refreshes them when a buffer grows or the writer
switches slots. `Complete()` commits the last row. The parameterless `GetRow()` overload remains available when a
cache is inconvenient.

The row writer handles row-group construction, encoding, and file finalization. `Complete()` commits the file. If
writing fails first, `Dispose()` stops the workers and releases resources without committing the incomplete file.
A disposed writer cannot be reused.
