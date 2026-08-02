# Schema

Plank can generate a Parquet schema from a C# type.

## Define a schema

Add [`[ParquetSchema]`](xref:Plank.Schema.ParquetSchemaAttribute) to a partial class:

```csharp
using Plank.Schema;

[ParquetSchema]
public sealed partial class EventSchema
{
    public int Id { get; init; }

    public byte[]? Name { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}
```

Each property becomes a column. Non-nullable properties are required and nullable properties are optional.

Plank generates the readers and writers for `EventSchema`. See [Reading](reading/index.md) and [Writing](writing.md) for usage.

## Customize a column

Use [`[ParquetColumn]`](xref:Plank.Schema.ParquetColumnAttribute) to change a column's name, logical type, physical type, or encoding:

```csharp
[ParquetColumn(
    "event_name",
    LogicalType = LogicalTypeKind.String,
    Encodings = [EncodingKind.RleDictionary])]
public byte[]? Name { get; init; }
```

Plank validates that the selected options are compatible with the property type.

## Supported types

| C# type | Parquet type |
| --- | --- |
| `bool` | `Boolean` |
| `byte`, `ushort`, `int`, `uint` | `Int32` |
| `long`, `ulong` | `Int64` |
| `float` | `Float` |
| `double` | `Double` |
| `string` | `ByteArray` with `String` |
| `byte[]`, `ReadOnlyMemory<byte>` | `ByteArray` |
| `Guid` | 16-byte `FixedLenByteArray` with `Uuid` |
| `DateOnly` | `Int32` with `Date` |
| `TimeOnly` | `Int64` with `Time` |
| `DateTime`, `DateTimeOffset` | `Int64` with `Timestamp` |

Nullable forms use the same type and create an optional column.

CLR strings require an explicit opt-in because UTF-8 encoding and decoding allocates:

```csharp
[ParquetSchema(AllowAllocatingValues = true)]
public sealed partial class SimpleSchema
{
    public string Name { get; init; } = string.Empty;

    public Guid Id { get; init; }
}
```

Without `AllowAllocatingValues`, the source generator reports an error for every `string` property. Use
`byte[]` or `ReadOnlyMemory<byte>` when allocation-free access is required.

## Runtime schemas

Use [`ParquetSchema`](xref:Plank.Schema.ParquetSchema) when a schema is created at runtime:

```csharp
var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int64),
    ColumnDefinition.OptionalLeaf(
        "name",
        ParquetPhysicalType.ByteArray,
        logicalType: new LogicalType.String())
]);
```

[`ColumnDefinition`](xref:Plank.Schema.ColumnDefinition) also supports groups, lists, and maps.

## Logical annotations

Runtime schemas preserve the complete parameters for the supported Parquet logical annotations. In addition to the
CLR-oriented string, integer, decimal, UUID, date, time, and timestamp types, Plank supports these raw-value
annotations:

| Logical type | Required physical shape |
| --- | --- |
| `Enum`, `Bson`, `Geometry`, `Geography` | `ByteArray` |
| `Float16` | 2-byte `FixedLenByteArray` |
| `Interval` | 12-byte `FixedLenByteArray` |
| `Unknown` | Any optional primitive column whose values are all null |
| `Variant` | A group containing required `metadata` and required or optional `value` byte-array fields |

`Geometry` and `Geography` retain an optional CRS, `Geography` retains its optional edge interpolation algorithm,
and `Variant` retains its optional specification version:

```csharp
var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf(
        "shape",
        ParquetPhysicalType.ByteArray,
        logicalType: new LogicalType.Geometry("EPSG:4326")),
    ColumnDefinition.OptionalGroup(
        "payload",
        new LogicalType.Variant(1),
        ColumnDefinition.RequiredLeaf("metadata", ParquetPhysicalType.ByteArray),
        ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.ByteArray))
]);
```

The physical metadata layer keeps CRS text in the footer buffer. Read it without allocating through
[`SchemaNodeLogicalTypeCrsUtf8`](xref:Plank.Reading.Physical.ParquetFileMetadata.SchemaNodeLogicalTypeCrsUtf8(System.Int32))
or [`ColumnLogicalTypeCrsUtf8`](xref:Plank.Reading.Physical.ParquetFileMetadata.ColumnLogicalTypeCrsUtf8(System.Int32)).

For generated flat schemas, marker annotations can be selected with `LogicalTypeKind`. `Float16` and `Interval`
automatically select fixed lengths of 2 and 12 bytes when the property uses a binary CLR carrier and explicitly
selects `FixedLenByteArray`. Parameterized geospatial and variant annotations use runtime schemas.
