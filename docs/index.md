# Plank

Plank is a Parquet reader and writer for .NET.

It provides several layers, from rows and datasets down to columns, pages, and file metadata. The higher-level APIs are easier to grasp and handle the Parquet details for you. Use the lower-level APIs when you need control over the file structure.

Declare schemas as C# types. Source generators create type-safe readers and writers, while analyzers report incompatible mappings at build time.

Start with the [schema guide](articles/schema.md), then choose the row, logical-column,
or physical API documented in the reading and writing sections.

Benchmarks and other experimental work are maintained separately in
[Plank-Lab](https://github.com/Kuinox/Plank-Lab).
