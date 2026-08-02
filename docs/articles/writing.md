# Writing

The writing APIs use the same source-generated [schema](schema.md) as the logical and row read layers.

## Format versions

`ParquetWriterOptions` controls the file-footer and data-page versions independently:

```csharp
var writer = schema.CreateWriter(stream, new ParquetWriterOptions
{
    FileVersion = ParquetFileVersion.V1,
    DataPageVersion = ParquetDataPageVersion.V2
});
```

The defaults preserve Plank's existing output: a V1 file footer and V2 data pages. V1 is the most widely compatible file-footer version. Data page V1 stores length-prefixed repetition and definition levels and compresses the complete page payload; data page V2 leaves levels uncompressed and compresses only encoded values. Both page versions support the same encodings, compression codecs, statistics, and page indexes.
