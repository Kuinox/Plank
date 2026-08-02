# Writing

The writing APIs use the same source-generated [schema](schema.md) as the logical and row read layers.

## Compression

Set `ParquetWriterOptions.Compression` and `CompressionLevel` to choose the default for every column in a file. A leaf column can override either setting through `ColumnOptions`:

```csharp
var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int64),
    ColumnDefinition.RequiredLeaf("payload", ParquetPhysicalType.ByteArray,
        new ColumnOptions(compression: CompressionKind.Zstd, compressionLevel: 9)),
    ColumnDefinition.RequiredLeaf("already_compressed", ParquetPhysicalType.ByteArray,
        new ColumnOptions(compression: CompressionKind.None))
]);

var writer = schema.CreateWriter(stream, new ParquetWriterOptions
{
    Compression = CompressionKind.Snappy
});
```

Here `id` uses Snappy, `payload` uses Zstandard level 9, and `already_compressed` is uncompressed. When a column overrides the codec but omits its level, Plank uses that codec's default level. A column-level setting that specifies only a level applies it to the writer's default codec. Unsupported levels are rejected when the writer is created.
