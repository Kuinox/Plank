# Writing

The writing APIs use the same source-generated [schema](schema.md) as the logical and row read layers.

## Declare row-group sorting

Use [`SortingColumns`](xref:Plank.Writing.ParquetWriterOptions.SortingColumns) to declare the lexicographic order of
every row group written by a writer:

```csharp
var options = new ParquetWriterOptions
{
    SortingColumns =
    [
        new ParquetSortingColumn(schema.LeafColumns[0].Ordinal),
        new ParquetSortingColumn(schema.LeafColumns[2].Ordinal, descending: true, nullsFirst: true)
    ]
};

using var stream = File.Create("events.parquet");
var writer = schema.CreateWriter(stream, options);
```

The declaration is metadata: Plank does not reorder values. Each row group supplied to the writer must already follow
the declared order. A column ordinal can appear only once and must refer to a leaf in the flattened schema.
