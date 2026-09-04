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
}

writer.Complete();
```

`writer.GetRow()` commits the previously returned row and returns a small view over the next reusable row buffer.
The stack-bound writer keeps GC-tracked managed references into the current column arrays, so generated property setters avoid repeated
array checks while remaining valid if a compacting GC moves the arrays. Use a row only until the next `GetRow()` call.
`Complete()` commits the last row.

The generated writer is a `ref struct`: keep it in synchronous code and finish its scope before an `await` or `yield` boundary.

The row writer handles row-group construction, encoding, and file finalization. `Complete()` commits the file. If
writing fails first, `Dispose()` stops the workers and releases resources without committing the incomplete file.
A disposed writer cannot be reused.
