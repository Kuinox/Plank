# Apache Arrow adapter

`Plank.Arrow` is a separate adapter package for moving flat Apache Arrow data between
[`RecordBatch`](https://arrow.apache.org/dotnet/current/api/Apache.Arrow.RecordBatch.html),
[`Table`](https://arrow.apache.org/dotnet/current/api/Apache.Arrow.Table.html), and Parquet. The core `Plank` package
does not depend on Apache Arrow.

## Write Arrow data

Create the writer from the Arrow schema, then add record batches. Each batch becomes one Parquet row group.

```csharp
using Apache.Arrow;
using Apache.Arrow.Types;
using Plank.Arrow;

var schema = new Schema([
    new Field("id", Int64Type.Default, nullable: false),
    new Field("name", StringType.Default, nullable: true)
], metadata: null);

using var batch = new RecordBatch(schema, [
    new Int64Array.Builder().Append(10).Append(20).Build(),
    new StringArray.Builder().Append("alpha").AppendNull().Build()
], length: 2);

using var output = File.Create("events.parquet");
using var writer = new ArrowParquetWriter(output, schema);
writer.WriteRecordBatch(batch);
```

`WriteTable(table)` writes the complete table as one row group and joins the chunks in each Arrow column. A writer
accepts only the exact field order, names, nullability, and Arrow types supplied to its constructor. Pass
`leaveOpen: true` when the destination stream must remain usable after the writer closes.

## Read Arrow data

Each Parquet row group is exposed as an independent record batch. `ReadTable()` keeps those row-group boundaries as
Arrow column chunks.

```csharp
using var input = File.OpenRead("events.parquet");
using var reader = new ArrowParquetReader(input);

using RecordBatch firstBatch = reader.ReadRecordBatch(0);
Table completeTable = reader.ReadTable();
```

The reader materializes Arrow-owned buffers, so returned batches and tables remain usable after the reader is
disposed. The input stream remains owned by the caller.

## Supported mappings

| Arrow type | Parquet storage |
| --- | --- |
| `Boolean` | `BOOLEAN` |
| `Int8`, `Int16`, `Int32` | `INT32` with signed integer annotation |
| `Int64` | `INT64` with signed integer annotation |
| `UInt8`, `UInt16`, `UInt32` | `INT32` with unsigned integer annotation |
| `UInt64` | `INT64` with unsigned integer annotation |
| `Float`, `Double` | `FLOAT`, `DOUBLE` |
| `String`, `Binary` | `BYTE_ARRAY` |
| `FixedSizeBinary` | `FIXED_LEN_BYTE_ARRAY` |
| `Guid` (`arrow.uuid`) | 16-byte `FIXED_LEN_BYTE_ARRAY` with UUID annotation |
| `Date32` | `INT32` with date annotation |
| `Time32` milliseconds | `INT32` with time annotation |
| `Time64` microseconds or nanoseconds | `INT64` with time annotation |
| `Timestamp` milliseconds, microseconds, or nanoseconds | `INT64` with timestamp annotation |

Nullable Arrow fields map to optional Parquet leaves. Required fields are checked for nulls before a row group is
written.

## Current limitations

- The adapter supports flat scalar fields. Nested, list, map, union, dictionary, decimal, large-offset, duration,
  interval, and null-only Arrow types are rejected.
- Arrow second-resolution timestamps and `Time32` seconds have no direct Parquet logical-type mapping and are rejected.
- Arrow time-of-day values map only to unadjusted Parquet time annotations; adjusted-to-UTC Parquet time columns are
  rejected because Arrow time types cannot retain that flag.
- Parquet stores only whether a timestamp is adjusted to UTC, not an Arrow timezone identifier. Any timezone-aware
  Arrow timestamp is read back with timezone `UTC`.
- Arrow and field metadata are not stored. Nested Parquet leaves are flattened to dot-separated field names when read.
- Record batches and tables are limited to `int.MaxValue` rows per write. `WriteTable` creates one row group.
- Conversion materializes Arrow buffers. Variable-width and fixed-binary writes copy each value into the representation
  accepted by Plank; this adapter is not a zero-copy boundary.
- The adapter is synchronous, matching Plank's allocation-conscious column APIs.
