# Row read layer

The row read layer exposes parquet data as strongly typed rows. It is built on top of the logical reader.

Use it when you want to ingest data as rows instead of columns.

The examples use the [`EventSchema`](../schema.md#define-a-schema) type declared in [Schema](../schema.md). The same declaration is also used by the logical read layer and the writing APIs.

## Read rows

Call [`CreateRowReader`](../schema.md#define-a-schema) and enumerate the reader:

[!code-csharp[](../../../Samples/Plank.Sample/RowApiSample.cs#ReadRows)]

The reader binds properties to file columns by name, so the columns do not need to appear in the same order in the file.

You can also use `MoveNext` and `Current` when explicit iteration is more convenient:

[!code-csharp[](../../../Samples/Plank.Sample/RowApiSample.cs#ReadExplicitly)]

> [!NOTE]
> A row is a temporary view over the reader's current buffers. Read its properties before advancing the reader. Binary properties return a scoped value whose bytes must be consumed before the reader advances.

Binary values expose their bytes through `Value` and their null state through `IsNull`.
Read the span directly to avoid allocating a string.

When the bytes must outlive the current iteration, copy the span into caller-owned storage before advancing the reader.

## Read selected properties

Pass a projection to decode only the properties you need:

[!code-csharp[](../../../Samples/Plank.Sample/RowApiSample.cs#ReadSelectedRows)]

Every property has a matching projection. Combine projections with `|`, or use `Projection.All` to select every property. Accessing a property that was not selected throws `InvalidOperationException`.

## Schema compatibility

By default, the row reader requires each selected column to exist and match the row schema. It validates physical type, logical type, and required or optional repetition.

For files whose schema evolves over time, pass [`ParquetSchemaEvolutionOptions`](xref:Plank.Reading.ParquetSchemaEvolutionOptions) to [`CreateRowReader`](../schema.md#define-a-schema). The options can allow selected compatibility changes, such as materializing a default value for a missing column or reading a required file column into a nullable property.

## Parallel decoding

`RowReaderOptions.Execution.WorkerCount` defaults to `Environment.ProcessorCount`.
Values greater than one enable background column decoding, with the worker count
capped by the number of projected file columns. Set it to one for synchronous
reading on the calling thread. Workers are reused when the reader is reset;
widening a projection can require a larger worker pool. `OnWorkerStarted` runs
once on each newly started worker.

With background workers, `MaxReadAheadRowGroups = 1` (the default) prepares the
first value buffer of each projected column in the next nonempty row group.
It retains at most one future row group alongside the current group; it does not
materialize entire row groups. Set it to zero to disable this prefetch while
retaining parallel decoding of the current group. Single-worker reading does not
prefetch. Rows remain in file order, and a prefetch failure is reported when the
reader reaches that row group.

Reset and disposal wait for outstanding reads before releasing buffers or changing
the source. Caller-supplied sources remain caller-owned. Arbitrary
`IParquetReadSource` implementations have their reads serialized internally;
decoding still runs concurrently. Custom buffer pools used with background workers
must support concurrent rentals and returns, as they must for parallel writers.
