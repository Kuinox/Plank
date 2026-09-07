# Physical read layer

The physical read layer is the lowest-level reader in Plank. It reads parquet file metadata and exposes encoded column data as raw bytes, without decoding it into C# values.

Use it when you need direct access to the raw structure of a parquet file, for example when building a parquet metadata viewer, analyzer, diagnostics tool, or custom reader.

## Open a file

```csharp
using Plank.Reading;
using Plank.Reading.Physical;

using var stream = File.OpenRead("events.parquet");
using ParquetFileReader reader = new();

reader.Reset(stream);
```

> [!NOTE]
> Reuse a single [`ParquetFileReader`](xref:Plank.Reading.Physical.ParquetFileReader) across files so it can reuse its stream wrapper and pooled metadata buffers instead of allocating them again.
>
> Call [`Reset(Stream)`](xref:Plank.Reading.Physical.ParquetFileReader.Reset(System.IO.Stream)) for each file. It attaches the reader, reads the footer, and makes the parsed metadata available.
>
> Reset does not allocate metadata buffers when the configured pool already has arrays large enough.
>
> Existing page cursors become invalid after a reset.

## Inspect metadata

```csharp
ParquetFileMetadata metadata = reader.Metadata;

for (var rowGroupOrdinal = 0; rowGroupOrdinal < metadata.RowGroupCount; rowGroupOrdinal++)
{
    ParquetRowGroupInfo rowGroup = metadata.RowGroup(rowGroupOrdinal);

    for (var columnOrdinal = 0; columnOrdinal < rowGroup.ColumnCount; columnOrdinal++)
    {
        ParquetColumnSchemaInfo column = metadata.ColumnSchema(columnOrdinal);
        ParquetColumnChunkInfo chunk = metadata.ColumnChunk(rowGroupOrdinal, columnOrdinal);
    }
}
```

[`Metadata`](xref:Plank.Reading.Physical.ParquetFileReader.Metadata) returns file-level metadata. It describes the schema, row groups, and column chunks, but it does not read column values.

## Read page bytes

```csharp
foreach (ParquetPage page in reader.OpenPages(rowGroupOrdinal, columnOrdinal))
{
    PageHeader header = page.Header;
    ReadOnlySpan<byte> payload = page.Payload;
}
```

[`OpenPages`](xref:Plank.Reading.Physical.ParquetFileReader.OpenPages(System.Int32,System.Int32)) returns a [`ParquetPageCursor`](xref:Plank.Reading.Physical.ParquetPageCursor) for one row-group column.

Each page exposes a parsed [`PageHeader`](xref:Plank.Reading.PageHeader) and a payload byte span. The payload is still parquet-encoded column data; dictionary encoding, levels, and values are decoded by the logical read layer.
