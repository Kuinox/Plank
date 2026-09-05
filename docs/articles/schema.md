# Schema

Declare a schema to keep the C# model and parquet file aligned on column names, types, and options. Plank uses that declaration to generate type-safe readers and writers and reports incompatible mappings at build time.

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

Plank generates the readers and writers for `EventSchema`. See [Reading](reading/index.md) and [Writing](writing/index.md) for usage.

Schema properties keep their declared names in generated row views and column selectors. If a generated
API name conflicts with your class or one of its members, Plank adds the first available numeric suffix.
For example, a property named `Row` is accessed as `writer.GetRow().Row`, and the generated row-view
type becomes `Row1`. A property named `All` remains `Projection.All`; the all-columns selector becomes
`Projection.All1`. This applies to both scalar and nested schemas, including generated factory methods.

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
