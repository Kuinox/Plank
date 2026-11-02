Plank is a high-performance, managed .NET implementation for reading and writing [Apache Parquet](https://parquet.apache.org/) files.

<p>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/plank-readme-dark.svg">
    <img src="assets/brand/plank-readme.svg" alt="Plank" width="960">
  </picture>
</p>

[Documentation](https://kuinox.github.io/Plank/) · [Benchmarks](https://kuinox.github.io/Plank-Lab/) · [NuGet](https://www.nuget.org/packages/Plank) · [Samples](Samples/Plank.Sample)

## Quick start

```sh
dotnet add package Plank
```

Declare a schema with `[ParquetSchema]`:

```csharp
using Plank.Schema;

[ParquetSchema]
public sealed partial class Measurement
{
    public int SensorId { get; init; }
    public double Value { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
```

The source generator adds strongly typed APIs to `Measurement`.

### Write

```csharp
using var output = File.Create("measurements.parquet");
using var writer = Measurement.CreateRowWriter(output);

for (var sensorId = 1; sensorId <= 3; sensorId++)
{
    var row = writer.GetRow();
    row.SensorId = sensorId;
    row.Value = 20.0 + sensorId;
    row.RecordedAt = DateTimeOffset.UtcNow;
}

writer.Complete();
```

### Read

```csharp
using var input = File.OpenRead("measurements.parquet");
using var reader = Measurement.CreateRowReader(input);

foreach (var row in reader)
    Console.WriteLine(
        $"{row.SensorId}: {row.Value} " +
        $"at {row.RecordedAt:O}");
```

## Features

- **Fast by design** Preallocated, reusable buffers keep steady-state reads and writes allocation-free. Encoding, decoding, compression, and decompression are parallelized across columns and row groups.
- **Source-generated APIs** Declare your Parquet schema as a C# model and get matching row readers and writers, dataset writers, projections, and column accessors at compile time.
- **APIs for every workload** Use rows for application-shaped data, columns for columnar workloads, datasets for partitioned outputs, or the physical API for low-level control.
- **Fine-grained configuration** Configure encodings and compression globally or per column, with control over row groups, pages, indexes, statistics, Bloom filters, and CRCs. Limit parallelism or use worker hooks to pin threads to specific CPUs.
- **Built for evolving data** Read selected columns, handle compatible schema changes, and define schemas dynamically at runtime.

## API guides

Most applications can stay with these generated row APIs. The dataset API routes rows into partitioned files, while the column and physical APIs provide progressively more control.

| Read Parquet | Write Parquet |
| :--- | :--- |
| [Row](https://kuinox.github.io/Plank/articles/reading/rows.html) | [Row](https://kuinox.github.io/Plank/articles/writing/rows.html) |
| [Logical columns](https://kuinox.github.io/Plank/articles/reading/logical.html) | [Logical columns](https://kuinox.github.io/Plank/articles/writing/logical.html) |
| [Physical files](https://kuinox.github.io/Plank/articles/reading/physical.html) | [Physical files](https://kuinox.github.io/Plank/articles/writing/physical.html) |
| | [Datasets](https://kuinox.github.io/Plank/articles/writing/datasets.html) |

## Plank Lab

[Plank Lab](https://github.com/Kuinox/Plank-Lab) is the deliberately experimental,
lower-quality, vibe-coded side of the project. It contains benchmarks, fuzzing tools, crash
investigations, and other research used to find performance issues, uncover bugs, and explore
ways to improve Plank. It is not production software.

The [published benchmark matrix](https://kuinox.github.io/Plank-Lab/) includes comparisons
across libraries, data shapes, and CPU architectures, along with the methodology and raw
results.

## Build and test

Plank currently targets .NET 10.

```sh
git clone --recurse-submodules https://github.com/Kuinox/Plank.git
cd Plank
dotnet test --solution Plank.sln --configuration Release
```

[MIT License](LICENSE)
