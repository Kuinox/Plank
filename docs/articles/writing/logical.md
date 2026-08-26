# Logical write layer

The logical write layer encodes typed column buffers using a declared schema. Each column can be prepared independently.

Use it when data is already organized by column. If each item contains all values for one row, use the [row write layer](rows.md).

## Write a row group

Create a writer from the schema and start a row group:

```csharp
using Plank.Sample;
using Plank.Schema;

using var stream = File.Create("events.parquet");
using var writer = EventSchema.CreateWriter(stream);

var rowGroup = writer.StartRowGroup();
```

Serialize values through the generated column properties:

```csharp
var now = DateTimeOffset.UtcNow;

rowGroup.Id.Serialize([1, 2, 3]);
rowGroup.Name.Serialize([
    "created"u8.ToArray(),
    "updated"u8.ToArray(),
    "deleted"u8.ToArray()
]);
rowGroup.OccurredAt.Serialize([now, now, now]);

rowGroup.Write(rowGroup.Id);
rowGroup.Write(rowGroup.Name);
rowGroup.Write(rowGroup.OccurredAt);
```

Each column can be serialized independently. Write them in schema order once they are ready.

## Runtime schemas

When a schema is created at runtime, select each leaf and specify its C# type explicitly:

```csharp
var runtimeSchema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32)
]);

using var runtimeStream = File.Create("runtime-events.parquet");
using var runtimeWriter = runtimeSchema.CreateWriter(runtimeStream);
var runtimeIds = runtimeWriter.CreateSerializedColumn<int>(runtimeSchema.LeafColumns[0]);

var runtimeRowGroup = runtimeWriter.StartRowGroup();
runtimeIds.Serialize([1, 2, 3]);
runtimeRowGroup.Write(runtimeIds);
runtimeWriter.CloseFile();
```

## Close the file

After all column buffers have been written, close the parquet file:

```csharp
writer.CloseFile();
```

`CloseFile` writes the footer and flushes the destination. If writing fails first, `Dispose()` releases resources
without writing a footer. A disposed writer cannot be reused.
