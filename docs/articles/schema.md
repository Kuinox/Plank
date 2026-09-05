# Schema

Declare a schema to keep the C# model and parquet file aligned on column names, types, and options. Plank uses that declaration to generate type-safe readers and writers and reports incompatible mappings at build time.

## Define a schema

Add `using Plank.Schema;` and apply [`[ParquetSchema]`](xref:Plank.Schema.ParquetSchemaAttribute)
to a partial class:

[!code-csharp[](../../Samples/Plank.Sample/EventSchema.cs#EventSchema)]

Each property becomes a column. Non-nullable properties are required and nullable properties are optional.

Plank generates the readers and writers for `EventSchema`. See [Reading](reading/index.md) and [Writing](writing/index.md) for usage.

## Customize a column

Use [`[ParquetColumn]`](xref:Plank.Schema.ParquetColumnAttribute) to change a column's name, logical type, physical type, or encoding:

```csharp
[ParquetColumn(
    "event_name",
    LogicalType = LogicalTypeKind.String,
    Encodings = [EncodingKind.RleDictionary],
    Compression = CompressionKind.Zstd,
    CompressionLevel = 3)]
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
| `decimal` | `FixedLenByteArray` with `Decimal` by default; specify precision and scale |
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

## Decimal values

Decimal properties require explicit `Precision` (total digits). Set `Scale` to the number of
fractional digits; its default is zero. By default, the generator selects a fixed byte width
that can hold that precision. Nullable decimal properties use the same settings and permit null values:

[!code-csharp[](../../Samples/Plank.Sample/DecimalApiSample.cs#DecimalSchema)]

This example stores amounts such as `12.34m` or `null`. Values must fit both the declared precision
and scale; serialization rejects precision loss and overflow instead of silently rounding.

## Timestamp offsets

`DateTimeOffset` columns preserve the instant and are read back with a UTC (`+00:00`) offset.
The original offset is not stored. If it matters to your application, store it in a separate column.
For example, `2026-01-02T12:30:00+02:00` reads back as `2026-01-02T10:30:00+00:00`.

## Runtime schemas

Use [`ParquetSchema`](xref:Plank.Schema.ParquetSchema) when a schema needs to be created at runtime:

```csharp
var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int64),
    ColumnDefinition.OptionalLeaf(
        "name",
        ParquetPhysicalType.ByteArray,
        logicalType: new LogicalType.String())
]);
```
