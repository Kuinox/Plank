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

TODO: write this page collaboratively.
