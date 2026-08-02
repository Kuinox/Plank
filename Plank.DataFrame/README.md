# Plank.DataFrame

`Plank.DataFrame` is the optional Microsoft.Data.Analysis adapter for Plank. It keeps DataFrame dependencies and
materialization allocations outside the performance-focused `Plank` core package.

```csharp
using Microsoft.Data.Analysis;
using Plank.DataFrame;

var frame = new DataFrame(
[
    new PrimitiveDataFrameColumn<int>("id", new[] { 1, 2, 3 }),
    new StringDataFrameColumn("name", new[] { "one", "two", "three" }),
    new BinaryDataFrameColumn("payload", new byte[][] { [1], [2], [3] })
]);

frame.WriteParquet(File.Create("rows.parquet"));

using var source = File.OpenRead("rows.parquet");
DataFrame restored = source.ReadDataFrame();
```

The synchronous adapter supports flat scalar columns containing booleans, signed and unsigned integers,
single/double precision values, strings, binary payloads, UUIDs, dates, times, local `DateTime` values, and
UTC-adjusted `DateTimeOffset` values. Nested/repeated columns and unsupported scalar types are rejected explicitly.

`WriteParquet` follows Plank's writer ownership and closes the destination stream after the footer is written.
`ReadDataFrame` leaves its source stream open.
