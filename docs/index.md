# Plank

Plank is a Parquet reader and writer for .NET.

It provides several layers, from rows and datasets down to columns, pages, and file metadata. The higher-level APIs are easier to grasp and handle the Parquet details for you. Use the lower-level APIs when you need control over the file structure.

Declare schemas as C# types. Source generators create type-safe readers and writers, while analyzers report incompatible mappings at build time.
