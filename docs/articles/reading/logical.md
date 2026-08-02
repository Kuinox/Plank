# Logical read layer

The logical read layer decodes parquet column data into typed C# values. It exposes values a column at a time, in buffers that follow the file's page boundaries.

Use it when you want decoded values without constructing rows. If you need raw page bytes and encoding metadata instead, use the [physical read layer](physical.md).

## Read with a schema

When you know which columns you need, use a reader generated from a [schema](../schema.md). It binds columns by name rather than file order, skips columns outside the schema, and exposes typed column properties on each row group:

```csharp
using Plank.Reading.Logical;

using var stream = File.OpenRead("events.parquet");
using EventSchema.Reader reader = EventSchema.CreateReader(stream);

foreach (EventSchema.ReadRowGroup rowGroup in reader.RowGroups)
    foreach (ColumnBuffer<int> buffer in rowGroup.IdColumn)
        foreach (int id in buffer.Values)
            Console.WriteLine(id);
```

If a schema is built at runtime instead, select its leaves through [`schema.LeafColumns`](xref:Plank.Schema.ParquetSchema.LeafColumns).

Strict validation is enabled by default. A requested column must exist in the file and have a compatible physical type, logical type, repetition, and fixed length. A required file column may be requested as optional, but an optional file column cannot be requested as required.

Set [`ParquetReaderOptions.Strict`](xref:Plank.Reading.Logical.ParquetReaderOptions.Strict) to `false` only when the caller will handle schema mismatches itself.

## Read without a schema

When the schema is not known ahead of time, let the reader discover it from the file:

```csharp
using Plank.Reading.Logical;
using Plank.Schema;

using var stream = File.OpenRead("events.parquet");
using ParquetReader reader = new();

reader.Reset(stream);

foreach (LeafColumn column in reader.Schema.LeafColumns)
    Console.WriteLine($"{column.Path}: {column.PhysicalType}");
```

[`Reset(Stream)`](xref:Plank.Reading.Logical.ParquetReader.Reset(System.IO.Stream)) reads the footer and binds the file schema. [`Schema`](xref:Plank.Reading.Logical.ParquetReader.Schema) is the schema used to read values, while [`Metadata.Schema`](xref:Plank.Reading.Logical.ParquetFileMetadata.Schema) always describes the complete schema stored in the file.

## Read column values

Enumerate the buffers exposed by the generated column property:

```csharp
using Plank.Reading.Logical;

foreach (EventSchema.ReadRowGroup rowGroup in reader.RowGroups)
{
    Console.WriteLine($"Rows: {rowGroup.RowCount}");

    foreach (ColumnBuffer<int> buffer in rowGroup.IdColumn)
        foreach (int id in buffer.Values)
            Console.WriteLine(id);
}
```

[`RowGroups`](xref:Plank.Reading.Logical.ParquetReader.RowGroups) can be enumerated or indexed. Each generated row group exposes its row count and a strongly typed [`RowGroupColumn<T>`](xref:Plank.Reading.Logical.RowGroupColumn`1) property for every schema property.

Column metadata also exposes Bloom filters without dropping to the physical reader:

```csharp
ParquetColumnChunkMetadata metadata = rowGroup.GetColumnMetadata(schema.LeafColumns[0]);
if (metadata.HasBloomFilter)
{
    using var filter = metadata.OpenBloomFilter();
    if (!filter.MightContain(42))
        return;
}
```

Each [`ColumnBuffer<T>`](xref:Plank.Reading.Logical.ColumnBuffer`1) contains one decoded batch. Consume [`Values`](xref:Plank.Reading.Logical.ColumnBuffer`1.Values) before advancing the column enumerator. The span is temporary and may refer to pooled storage that is reused for the next buffer.

Nullable schema properties generate nullable column types such as [`RowGroupColumn<int?>`](xref:Plank.Reading.Logical.RowGroupColumn`1).

## Read binary values

Binary columns use the same generic column API as other unmanaged values. Each generated binary
column is a [`RowGroupColumn<byte>`](xref:Plank.Reading.Logical.RowGroupColumn`1):

```csharp
foreach (ColumnBuffer<byte> buffer in rowGroup.NameColumn)
{
    for (int i = 0; i < buffer.Count; i++)
    {
        if (buffer.IsNull(i))
            continue;

        Consume(buffer.GetValue(i));
    }
}
```

Each page stores its internal descriptor table and all referenced bytes in one pooled
[`ParquetBuffer`](xref:Plank.Writing.ParquetBuffer). An empty value has a zero-length span while
`IsNull(i)` returns `false`; optional nulls return `true`.

For binary columns, `Count` is the number of logical byte arrays, `GetValue(i)` preserves their
individual boundaries, and `Values` is the concatenated non-null byte payload.

Call `buffer.GetValue(i).ToArray()` only when an owning `byte[]` is required. That call is the
explicit allocation boundary. Runtime schemas can request the same zero-allocation view with
`rowGroup.Column<byte>(column)`.

## Retain a buffer

Value buffers can be retained beyond the current enumeration step:

```csharp
using Plank.Reading.Logical;
using Plank.Writing;

foreach (ColumnBuffer<int> buffer in rowGroup.IdColumn)
{
    using ParquetBuffer retained = buffer.Retain();
    foreach (int id in retained.AsSpan<int>())
        Console.WriteLine(id);
}
```

[`Retain`](xref:Plank.Reading.Logical.ColumnBuffer`1.Retain) returns a reference-counted
[`ParquetBuffer`](xref:Plank.Writing.ParquetBuffer); dispose it when it is no longer needed.

Keep a binary `ColumnBuffer<byte>` value and its retained lease
together while accessing values:

```csharp
ColumnBuffer<byte> buffer = binaryBuffers.Current;
using ParquetBuffer retained = buffer.Retain();
Consume(buffer.GetValue(0));
```
