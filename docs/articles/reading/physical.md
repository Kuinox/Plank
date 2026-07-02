# Physical reader

The physical reader is the lowest user-facing reader layer. It exposes parquet metadata, schema nodes, row groups, column chunks, page headers, and page payloads without binding the file to a requested `ParquetSchema`.

Use it when you are building inspection tools, custom decoders, diagnostics, or allocation-sensitive integrations that need direct access to parquet structure.

```csharp
using Plank.Reading;

using var stream = File.OpenRead("events.parquet");
using var reader = new ParquetFileReader();

reader.Reset(stream);

for (var rowGroupOrdinal = 0; rowGroupOrdinal < reader.RowGroupCount; rowGroupOrdinal++)
{
    var rowGroup = reader.RowGroup(rowGroupOrdinal);

    for (var columnOrdinal = 0; columnOrdinal < rowGroup.ColumnCount; columnOrdinal++)
    {
        var chunk = rowGroup.ColumnChunk(columnOrdinal);
        using var pages = rowGroup.OpenPages(columnOrdinal);

        while (pages.MoveNext())
        {
            var header = pages.CurrentHeader;
            var payload = pages.CurrentPayload;
        }
    }
}
```

Use `ParquetFileReaderOptions` to provide a custom buffer pool when integrating with an existing allocation strategy.

```csharp
using Plank.Reading;
using Plank.Writing;

var options = new ParquetFileReaderOptions
{
    BufferPool = DefaultParquetBufferPool.Shared
};
```

This layer does not project a logical application schema. It tells you what is physically present in the file.
