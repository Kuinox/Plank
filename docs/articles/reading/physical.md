# Physical read layer

The physical read layer is the lowest-level reader in Plank. It reads parquet file metadata and exposes encoded column data as raw bytes, without decoding it into C# values.

Use it when you need direct access to the raw structure of a parquet file, for example when building a parquet viewer, analyzer, diagnostics tool, or custom reader.

## Open a file

```csharp
using Plank.Reading;
using Plank.Reading.Physical;

using var stream = File.OpenRead("events.parquet");
using ParquetFileReader reader = new();

reader.Reset(stream);
```

[`ParquetFileReader`](xref:Plank.Reading.Physical.ParquetFileReader) is reusable. [`Reset(Stream)`](xref:Plank.Reading.Physical.ParquetFileReader.Reset(System.IO.Stream)) attaches it to a file, reads the footer, and makes the parsed metadata available.

Call [`Reset`](xref:Plank.Reading.Physical.ParquetFileReader.Reset*) again to reuse the same reader for another file.

After the first stream reset, the reader keeps the same stream wrapper. Metadata buffers come from the configured pool, so reset does not allocate them when the pool already has arrays big enough.

Existing page cursors become invalid after a reset.

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

The same object exposes `created_by` and ordered key-value metadata without decoding strings or allocating a
dictionary:

```csharp
ReadOnlySpan<byte> createdBy = metadata.CreatedByUtf8;
for (var i = 0; i < metadata.KeyValueMetadataCount; i++)
{
    ReadOnlySpan<byte> key = metadata.KeyValueMetadataKeyUtf8(i);
    ReadOnlySpan<byte> value = metadata.KeyValueMetadataValueUtf8(i);
    bool hasValue = metadata.KeyValueMetadata[i].HasValue;
}
```

Use `HasCreatedBy` and each entry's `HasValue` property to distinguish an omitted value from an empty UTF-8 value.

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
