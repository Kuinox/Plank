# Microsoft.Data.Analysis DataFrames

The optional `Plank.DataFrame` package reads and writes flat
[`Microsoft.Data.Analysis.DataFrame`](https://learn.microsoft.com/dotnet/api/microsoft.data.analysis.dataframe)
instances without adding DataFrame dependencies or convenience-layer allocations to the core `Plank` package.
The integration is synchronous, matching Plank's column APIs.

## Write a DataFrame

```csharp
using Microsoft.Data.Analysis;
using Plank.DataFrame;

var frame = new DataFrame(
[
    new PrimitiveDataFrameColumn<int>("id", new[] { 1, 2, 3 }),
    new StringDataFrameColumn("name", new[] { "one", "two", "three" }),
    new BinaryDataFrameColumn("payload", new byte[][] { [1], [2], [3] })
]);

frame.WriteParquet(File.Create("rows.parquet"), rowGroupSize: 64 * 1024);
```

`WriteParquet` infers required columns when `NullCount` is zero and optional columns otherwise. The optional
`ParquetWriterOptions` argument controls Plank compression, page indexes, CRCs, buffer pools, and other writer
behavior. `rowGroupSize` controls how the DataFrame is divided into row groups.

Plank's writer closes the destination stream after writing the footer, so `WriteParquet` does too.

## Read a DataFrame

```csharp
using Plank.DataFrame;

using var source = File.OpenRead("rows.parquet");
Microsoft.Data.Analysis.DataFrame frame = source.ReadDataFrame();
```

`ReadDataFrame` discovers the Parquet schema, materializes all row groups, and leaves the input stream open. An
existing reusable `Plank.Reading.Logical.ParquetReader` can instead be materialized with `reader.ToDataFrame()`.

## Supported scalar mappings

| DataFrame value type | Parquet representation |
| --- | --- |
| `bool` | `BOOLEAN` |
| `byte`, `sbyte`, `short`, `ushort`, `int`, `uint` | `INT32` with integer annotations where needed |
| `long`, `ulong` | `INT64` with an unsigned annotation for `ulong` |
| `float`, `double` | `FLOAT`, `DOUBLE` |
| `string` | UTF-8 `BYTE_ARRAY` with the string annotation |
| `byte[]` via `BinaryDataFrameColumn` | Unannotated `BYTE_ARRAY` |
| `Guid` | 16-byte fixed-length UUID |
| `DateOnly` | Annotated `INT32` date |
| `TimeOnly` | Microsecond `INT64` time |
| `DateTime` | Microsecond local timestamp |
| `DateTimeOffset` | Microsecond UTC-adjusted timestamp |

DataFrame has no built-in binary column, so the adapter supplies `BinaryDataFrameColumn`. Raw `BYTE_ARRAY`,
`FIXED_LEN_BYTE_ARRAY`, and `INT96` fields materialize into this type; string/JSON and UUID annotations select their
corresponding higher-level columns instead.

`DateTime` values written as local timestamps must have `DateTimeKind.Unspecified`. `DateTimeOffset` values are
normalized to their UTC instant by the Parquet timestamp representation.

The adapter deliberately supports flat scalar DataFrames. Nested groups, lists, maps, repeated fields, `decimal`,
`char`, and unknown logical annotations throw `NotSupportedException` rather than being silently skipped or coerced.
