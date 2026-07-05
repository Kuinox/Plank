# Physical read layer

The physical read layer is the lowest-level reader in Plank. It reads parquet file metadata and exposes encoded column data as raw bytes, without decoding it into C# values.

Use it when you need direct access to the raw structure of a parquet file, for example when building a parquet viewer, analyzer, diagnostics tool, or custom reader.

## Open a file

```csharp
using Plank.Reading.Physical;

using var stream = File.OpenRead("events.parquet");
using var reader = new ParquetFileReader();

reader.Reset(stream);
```

[`ParquetFileReader`](xref:Plank.Reading.Physical.ParquetFileReader) is reusable. [`Reset(Stream)`](xref:Plank.Reading.Physical.ParquetFileReader.Reset(System.IO.Stream)) attaches it to a file, reads the footer, and makes the parsed metadata available.

Call `Reset` again to reuse the same reader for another file.

After the first stream reset, the reader keeps the same stream wrapper. Metadata buffers come from the configured pool, so reset does not allocate them when the pool already has arrays big enough.

Existing page cursors become invalid after a reset.

## Inspect metadata

```csharp
var metadata = reader.Metadata;

for (var rowGroupOrdinal = 0; rowGroupOrdinal < metadata.RowGroupCount; rowGroupOrdinal++)
{
    var rowGroup = metadata.RowGroup(rowGroupOrdinal);

    for (var columnOrdinal = 0; columnOrdinal < rowGroup.ColumnCount; columnOrdinal++)
    {
        var column = metadata.ColumnSchema(columnOrdinal);
        var chunk = metadata.ColumnChunk(rowGroupOrdinal, columnOrdinal);
    }
}
```

[`Metadata`](xref:Plank.Reading.Physical.ParquetFileReader.Metadata) returns file-level metadata. It describes the schema, row groups, and column chunks, but it does not read column values.

## Read page bytes

```csharp
foreach (var page in reader.OpenPages(rowGroupOrdinal, columnOrdinal))
{
    var header = page.Header;
    var payload = page.Payload;
}
```

[`OpenPages`](xref:Plank.Reading.Physical.ParquetFileReader.OpenPages(System.Int32,System.Int32)) returns a [`ParquetPageCursor`](xref:Plank.Reading.Physical.ParquetPageCursor) for one row-group column.

Each page exposes a parsed `PageHeader` and a payload byte span. The payload is still parquet-encoded column data; dictionary encoding, levels, and values are decoded by the logical read layer.
