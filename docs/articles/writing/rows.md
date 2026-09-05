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
Assign the generated row properties to write into their corresponding column buffers.
Use a row only until the next `GetRow()`, `Complete()`, `Reset()`, or `Dispose()` call.
`Complete()` commits the last row.

The generated `PipelineWriter` is a class: it can be stored in a field and reused with `Reset()`.
Only the temporary `Row` view is a `ref struct`; it cannot be kept across an `await` or `yield` boundary.

## Reuse a cursor in a hot loop

For repeated assignments, keep one mutable cursor outside the loop:

```csharp
var row = writer.CreateCursor();
for (var id = 0; id < 100; id++)
{
    row.NextRow();
    row.Id = id;
    row.Name = "event"u8.ToArray();
    row.OccurredAt = DateTimeOffset.UtcNow;
}
writer.Complete();
```

`CreateCursor()` does not reserve a row. `NextRow()` commits the previous pending row and
positions the cursor on the next row; `Complete()` commits the last one. Do not assign
properties before the first successful `NextRow()`. After another cursor or `GetRow()`
advances the writer, call `NextRow()` on this cursor before assigning again.

The cursor retains one GC-tracked managed ref per column, initialized on its first
`NextRow()` and rebound when a buffer grows or the writer switches slots. Setters use
these refs directly, without array null probes or bounds checks. Row advancement still
validates writer state and capacity; variable-width row-size accounting is unchanged.
Even a wide cursor is initialized only once, not copied for every row. Pass it to
helpers with `ref` and avoid by-value copies or readonly receivers.

`PipelineWriter` remains a class. Only `RowCursor` is a mutable `ref struct`, so
create a new local cursor after an `await` or `yield`. A cursor can be reused after
`Reset()` by calling `NextRow()` again. Do not assign through a cursor after the
writer advances, completes, resets, or is disposed until it is positioned again.
Neither the writer nor its cursors support concurrent writes.

The row writer handles row-group construction, encoding, and file finalization. `Complete()` commits the file. If
writing fails first, `Dispose()` stops the workers and releases resources without committing the incomplete file.
A disposed writer cannot be reused.
