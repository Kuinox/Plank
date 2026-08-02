# Plank.Arrow

`Plank.Arrow` is the optional Apache Arrow adapter for Plank. It reads Parquet row groups as Arrow `RecordBatch`
instances, combines them into `Table` instances, and writes Arrow batches or tables through Plank without adding an
Apache Arrow dependency to the core `Plank` package.

```csharp
using Apache.Arrow;
using Plank.Arrow;

using var output = File.Create("data.parquet");
using var writer = new ArrowParquetWriter(output, recordBatch.Schema);
writer.WriteRecordBatch(recordBatch);
```

The adapter supports flat nullable scalar columns, strings and binary values, fixed-size binary and UUID values,
`Date32`, supported Arrow time units, and millisecond/microsecond/nanosecond timestamps. See the Plank documentation
for the complete mapping and current limitations.
