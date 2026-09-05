# Dataset writer layer

The dataset writer sits above the [row write layer](rows.md). It routes mixed rows to multiple
Parquet files while keeping only a fixed number open at a time. Each output file follows the
same row-group and rollover targets as the row writer.

The examples use [EventSchema](../schema.md#define-a-schema) and the `FileParquetSource`
adapter below. Add `using Plank;`, `using Plank.Writing;`, and `using System.Text;`.

## Provide reusable file sources

A dataset writer needs sources that can open, read, write, close, and reopen paths. Copy this
local-file adapter into your project, or implement `IParquetReadWriteSource` for your storage:

[!code-csharp[](../../../Samples/Plank.Sample/FileParquetSource.cs#FileParquetSource)]

The adapter creates missing parent directories and respects the `FileMode` requested by Plank.
Each source owns at most one open file. Dispose the writer before disposing its sources.

## Route rows

The route returns the UTF-8 path that should receive each row. Static UTF-8 paths do not need
an allocation. This complete write operation uses one source to handle two output paths:

[!code-csharp[](../../../Samples/Plank.Sample/DatasetApiSample.cs#StaticDataset)]

`files.Length` limits the number of files kept open; it does not limit the number of partitions.
Plank closes and reuses a source when another path needs it. `Queue()` copies each row into
writer buffers. Disposing the writer writes remaining rows and closes all open files.

Paths here are relative to the process working directory. Use a fresh output directory for a
new dataset: if a path already contains a compatible Parquet file, Plank appends to it. Running
the example again adds the rows again; it does not overwrite or deduplicate them.

## Build paths at runtime

For dynamic paths, return storage rented from the provided buffer pool and hand its ownership
to Plank through `allocation`:

[!code-csharp[](../../../Samples/Plank.Sample/DatasetApiSample.cs#AllocatedDataset)]

Plank releases the returned allocation when it no longer needs the path. Do not dispose that
buffer or reuse its span after returning it.

The [runnable sample](https://github.com/Kuinox/Plank/tree/master/Samples/Plank.Sample)
reads back each partition and checks both source reuse and appending on a second run.
