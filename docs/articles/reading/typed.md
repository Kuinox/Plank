# Typed column reader

The typed column reader binds a parquet file to a requested `ParquetSchema`. It sits above the physical reader and exposes row groups, typed columns, and typed column pages.

Use it when you want schema-aware column reads without using the generated row reader.

```csharp
using Plank.Reading;
using Plank.Schema;

var schema = RowSchema.Create(
    RowSchema.Column<int>("id", ParquetPhysicalType.Int32),
    RowSchema.Column<string>("name", ParquetPhysicalType.ByteArray, logicalType: new LogicalType.String())
).ParquetSchema;

using var stream = File.OpenRead("events.parquet");
using var reader = schema.CreateReader(stream);

foreach (var token in reader.EnumerateRowGroups())
{
    using var rowGroup = reader.OpenRowGroup(stream, token);
    var idPages = rowGroup.Column<int>(schema.Columns[0]).Pages;

    foreach (var page in idPages)
    {
        var values = page.Values.Span;
    }
}
```

`ParquetReaderOptions.Strict` controls whether schema binding rejects extra or missing file columns. `ParquetReaderOptions.BufferPool` lets you reuse the same allocation strategy as the writer and physical reader.

Use this layer for projection, typed page enumeration, and custom row materialization.
