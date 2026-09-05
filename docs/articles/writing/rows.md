# Row write layer

The row write layer ingests rows and automatically creates row groups around the target size.

Use this layer when you want to produce good Parquet files without managing their structure yourself.

By default, row groups target [128 MiB](https://iceberg.apache.org/docs/latest/configuration/#write-properties).

It can also roll over to a new file around the [512 MiB](https://iceberg.apache.org/docs/latest/configuration/#write-properties) default target with `EventSchema.CreateRowWriter(filePath, file)`.

## Write rows

The example uses [EventSchema](../schema.md#define-a-schema). Set `path` to the output file, such as `"events.parquet"`.

[!code-csharp[](../../../Samples/Plank.Sample/RowApiSample.cs#WriteRows)]

`GetRow()` commits the previously returned row and returns the next reusable row buffer. `Complete()` commits the
last row.

The row writer handles row-group construction, encoding, and file finalization. `Complete()` commits the file. If
writing fails first, `Dispose()` stops the workers and releases resources without committing the incomplete file.
A disposed writer cannot be reused.
