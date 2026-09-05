# Dataset writer layer

The dataset writer sits above the [row write layer](rows.md). It routes rows to multiple Parquet files.

Use it to write a partitioned dataset when rows belonging to different files are mixed together.

It can write any number of output files while keeping only a fixed number open at a time.

Each output file follows the same row-group and rollover targets as the row writer.

The examples use [EventSchema](../schema.md#define-a-schema) and the `FileParquetSource`
adapter below. Add `using Plank;`, `using Plank.Writing;`, and `using System.Text;`.

## File sources

Implement `IParquetReadWriteSource` to open and reopen dataset files. This example uses local files:

[!code-csharp[](../../../Samples/Plank.Sample/FileParquetSource.cs#FileParquetSource)]

Dispose the writer before disposing its sources.

## Route rows

The route returns the UTF-8 path that should receive each row:

[!code-csharp[](../../../Samples/Plank.Sample/DatasetApiSample.cs#StaticDataset)]

`files` contains the reusable read/write sources. Its length is the maximum number of files kept open.

`Queue()` copies the row into the writer buffers. Disposing the writer writes the remaining rows and closes every open file.

Plank appends to existing files. Use a new directory to start a fresh dataset.

## Build paths at runtime

Static UTF-8 paths do not need an allocation. For paths built at runtime, use the provided buffer pool:

[!code-csharp[](../../../Samples/Plank.Sample/DatasetApiSample.cs#AllocatedDataset)]

Plank releases the returned allocation when it no longer needs the path.
