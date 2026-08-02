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

## Custom value mappings

Derive from [`ParquetValueConverter<TValue, TPhysical>`](xref:Plank.Schema.ParquetValueConverter`2) to map an
unmanaged domain value to a CLR type that Plank already supports. The physical CLR type determines the default
Parquet physical and logical types:

```csharp
public readonly record struct OrderId(long Value);

public sealed class OrderIdConverter : ParquetValueConverter<OrderId, long>
{
    public override long ConvertToPhysical(OrderId value) => value.Value;

    public override OrderId ConvertFromPhysical(long value) => new(value);
}
```

Attach the converter to a generated column with `Converter`. The converter must be a non-abstract type with an
accessible parameterless constructor:

```csharp
[ParquetColumn(Converter = typeof(OrderIdConverter),
    Encodings = [EncodingKind.DeltaBinaryPacked])]
public OrderId Id { get; init; }

[ParquetColumn(Converter = typeof(OrderIdConverter))]
public OrderId? ParentId { get; init; }
```

Plank preserves nulls without invoking the converter. Conversion uses pooled unmanaged scratch buffers; override the
span overloads when a mapping can convert a batch more efficiently. Converter instances can be shared by concurrent
pipeline workers, so implementations must be thread-safe. Generated column readers, row readers, column writers, and
row writers all use the declared converter.

The same mechanism supplies an interoperable `decimal` mapping. Scale the value in the converter and declare the
matching Parquet decimal metadata on the property:

```csharp
public sealed class MoneyConverter : ParquetValueConverter<decimal, long>
{
    public override long ConvertToPhysical(decimal value) => decimal.ToInt64(value * 100m);

    public override decimal ConvertFromPhysical(long value) => value / 100m;
}

[ParquetColumn(Converter = typeof(MoneyConverter), LogicalType = LogicalTypeKind.Decimal,
    DecimalPrecision = 18, DecimalScale = 2)]
public decimal Amount { get; init; }
```

The converter's scaling must agree with `DecimalScale`; overflow and rounding policy remain application-defined.

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

Runtime schemas accept the same converter instance on leaf definitions:

```csharp
var converter = new OrderIdConverter();
var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf(
        "id",
        ParquetPhysicalType.Int64,
        converter: converter),
    ColumnDefinition.OptionalLeaf(
        "parent_id",
        ParquetPhysicalType.Int64,
        converter: converter)
]);
```

Create both the writer and logical reader from that schema so the typed column APIs retain the mapping.

[`ColumnDefinition`](xref:Plank.Schema.ColumnDefinition) also supports groups, lists, and maps.
