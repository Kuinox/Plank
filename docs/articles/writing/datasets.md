# Dataset writer layer

The dataset writer sits above the [row write layer](rows.md). It routes rows to multiple Parquet files.

Use it to write a partitioned dataset when rows belonging to different files are mixed together.

It can write any number of output files while keeping only a fixed number open at a time.

Each output file follows the same row-group and rollover targets as the row writer.

## Route rows

The route returns the UTF-8 path that should receive each row:

```csharp-invisible
IParquetReadWriteSource[] files = [file1, file2];
```

```csharp
var writer = EventSchema.CreateDatasetWriter(
    static (row, _, out ParquetBuffer? allocation) =>
    {
        allocation = null;
        return row.Id % 2 == 0
            ? "events/even.parquet"u8
            : "events/odd.parquet"u8;
    },
    files);
```

Static UTF-8 paths do not need an allocation. For paths built at runtime, use the provided buffer pool:

```csharp
var pooledWriter = EventSchema.CreateDatasetWriter(
    static (row, pool, out ParquetBuffer? allocation) =>
    {
        var path = $"events/bucket={row.Id % 16}.parquet";
        var buffer = pool.Rent(checked((uint)Encoding.UTF8.GetByteCount(path)));
        var length = Encoding.UTF8.GetBytes(path, buffer.Span);
        allocation = buffer;
        return buffer.Span[..length];
    },
    files);
```

Plank releases the returned allocation when it no longer needs the path.

`files` contains the reusable read/write sources. Its length is the maximum number of files kept open.

## Write rows

Queue rows in any mix:

```csharp
foreach (EventSchema row in events)
    writer.Queue(row);
```

`Queue()` copies the row into the writer buffers.

## Dispose the writer

```csharp
writer.Dispose();
```

This writes the remaining rows and closes every open file.
