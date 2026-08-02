# Writing

The writing APIs use the same source-generated [schema](schema.md) as the logical and row read layers.

## File metadata

Set `CreatedBy` and ordered key-value metadata through `ParquetWriterOptions`:

```csharp
ParquetWriterOptions options = new()
{
    CreatedBy = "event-exporter 2.1",
    KeyValueMetadata =
    [
        new("source", "telemetry"),
        new("schema-version", "4")
    ]
};
```

Keys retain their order and may be repeated. Values may be `null`, matching the Parquet metadata model. Set
`CreatedBy` to `null` to omit the field.

## Append row groups to an existing file

Open an existing file with read, write, and seek access, then create an appender from the same schema:

```csharp
using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
ParquetWriter writer = schema.CreateAppender(stream);

RowGroupWriter rowGroup = writer.StartRowGroup();
// Serialize and write every schema column.
writer.CloseFile();
```

The appender validates the complete physical and logical schema before modifying the file. It retains existing row
groups, replaces the old footer, and preserves file key-value metadata by default. Supply `ParquetAppendOptions` to
configure new row groups or replace the existing metadata.

TODO: write this page collaboratively.
