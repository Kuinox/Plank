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
| `decimal` | `FixedLenByteArray` with `Decimal` |
| `string` | `ByteArray` with `String` |
| `byte[]`, `ReadOnlyMemory<byte>` | `ByteArray` |
| `Guid` | 16-byte `FixedLenByteArray` with `Uuid` |
| `DateOnly` | `Int32` with `Date` |
| `TimeOnly` | `Int64` with `Time` |
| `DateTime`, `DateTimeOffset` | `Int64` with `Timestamp` |

Nullable forms use the same type and create an optional column.

Decimal columns require an explicit precision and may set a scale (which defaults to zero):

```csharp
[ParquetColumn(Precision = 18, Scale = 4)]
public decimal Amount { get; init; }
```

The generated schema chooses the smallest fixed-length byte width that can hold the declared precision. You can
override the physical type with the `ParquetColumn` constructor when interoperability requires `Int32`, `Int64`, or
`ByteArray`. `System.Decimal` supports precision up to 29 and scale up to 28. Serialization is exact: a value that
has too many fractional digits or exceeds the declared precision is rejected instead of rounded.

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

For a runtime decimal schema, supply the physical width and decimal annotation explicitly:

```csharp
var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf(
        "amount",
        ParquetPhysicalType.FixedLenByteArray,
        new ColumnOptions(typeLength: 8),
        new LogicalType.Decimal(18, 4))
]);
```

[`ColumnDefinition`](xref:Plank.Schema.ColumnDefinition) also supports groups, lists, and maps.
