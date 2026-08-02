# Untyped rows

The `Plank.Untyped` package reads files whose schema is unknown at compile time into dictionary-based rows. It also
writes dictionaries against an explicit runtime [`ParquetSchema`](xref:Plank.Schema.ParquetSchema). Keeping this
adapter in a separate package prevents its convenience allocations and reflection cache from affecting Plank's core
column and generated-row APIs.

## Read an unknown file

Reset a reusable [`ParquetUntypedReader`](xref:Plank.Untyped.ParquetUntypedReader), then read the complete file or one
row group:

```csharp
using Plank.Untyped;

using var stream = File.OpenRead("events.parquet");
using var reader = new ParquetUntypedReader();
reader.Reset(stream);

foreach (IReadOnlyDictionary<string, object?> row in reader.ReadAll())
    Console.WriteLine($"{row["id"]}: {row["name"]}");
```

The reader discovers the runtime schema and materializes logical values where Plank has a direct CLR mapping.
Groups become `Dictionary<string, object?>`, lists become `List<object?>`, and maps become
`Dictionary<object, object?>`. Raw `BYTE_ARRAY`, `FIXED_LEN_BYTE_ARRAY`, and `INT96` values become `byte[]` unless a
string, JSON, or UUID annotation supplies a more specific mapping.

`ReadRowGroup(index)` avoids retaining rows from the rest of the file. Both read methods intentionally allocate the
returned object graph; use the logical column or generated-row APIs when steady-state allocation is important.

## Write dictionaries

Writing requires an explicit schema so nulls and numeric values have unambiguous Parquet types:

```csharp
var schema = new ParquetSchema([
    ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32),
    ColumnDefinition.OptionalLeaf("name", ParquetPhysicalType.ByteArray,
        logicalType: new LogicalType.String())
]);

var writer = new ParquetUntypedWriter(File.Create("events.parquet"), schema);
writer.WriteRowGroup([
    new Dictionary<string, object?> { ["id"] = 1, ["name"] = "created" },
    new Dictionary<string, object?> { ["id"] = 2, ["name"] = null }
]);
writer.CloseFile();
```

Nested group values must implement `IReadOnlyDictionary<string, object?>`; list values may be arrays or any other
`IEnumerable`; map values may implement `IDictionary` or `IEnumerable<KeyValuePair<object, object?>>`. Input
enumerables are canonicalized before a row group is opened, so invalid required values do not leave a partially open
row group.

The writer supports the nested shapes accepted by Plank's column writer: required groups, optional flat leaves,
required or optional lists and maps, direct optional list/map elements, and required nested lists. It rejects optional
group ancestors, optional members inside repeated records, and optional inner containers up front because those shapes
cannot yet be represented by the underlying column writer.
