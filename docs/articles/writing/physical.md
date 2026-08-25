# Physical write layer

Use the physical write layer when you need fine control over the structure of a parquet file.

The other write layers are built on top of it.

## Write a file

A parquet file contains row groups. Each row group contains one column chunk per column.

Create the schema, writer, and reusable column buffers before starting a row group:

```csharp
using Plank.Schema;

var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32),
    ColumnDefinition.RequiredLeaf("name", ParquetPhysicalType.ByteArray)
]);

using var stream = File.Create("events.parquet");
var writer = schema.CreateWriter(stream);

var ids = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
var names = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[1]);

var rowGroup = writer.StartRowGroup();
```

## Write columns

Serialize values into each column buffer, then write it:

```csharp
ids.Serialize([1, 2, 3]);
rowGroup.Write(ids);

names.Serialize([
    "created"u8.ToArray(),
    "updated"u8.ToArray(),
    "deleted"u8.ToArray()
]);
rowGroup.Write(names);
```

> [!NOTE]
> Encoding and compression happen in `Serialize`. They can be done in advance or in parallel. `Write` only writes the prepared pages.

`Write` consumes the buffered data, so the same columns can be reused for the next row group.

Columns must be written in schema order and contain the same number of rows.

## Finish the file

Call `CloseFile` after writing all row groups:

```csharp
writer.CloseFile();
```

`CloseFile` writes the footer and flushes the destination. It throws if the current row group is missing columns.

## Reuse the writer

The writer and its column buffers can be reused for another file.

After `CloseFile`, attach the writer to another stream with `Reset`:

```csharp
using var nextStream = File.Create("next.parquet");
writer.Reset(nextStream);

var rowGroup = writer.StartRowGroup();
```

The `ids` and `names` column buffers can now be serialized and written again.
