# Plank

Plank is a Parquet reader and writer for .NET.

It provides several layers, from rows and datasets down to columns, pages, and file metadata. The higher-level APIs are easier to grasp and handle the Parquet details for you. Use the lower-level APIs when you need control over the file structure.

Declare schemas as C# types. Source generators create type-safe readers and writers, while analyzers report incompatible mappings at build time.

Start with the [schema guide](articles/schema.md), then choose the row, logical-column,
or physical API documented in the reading and writing sections.

## Fast. Really fast.

Writing Parquet should not be the slow part of your pipeline. These results include the
complete in-memory file write, from writer creation to the footer and close.

The benchmark data and renderer live in the public
[Plank-Lab repository](https://github.com/Kuinox/Plank-Lab). You can also
[open the benchmark matrix directly](https://kuinox.github.io/Plank-Lab/).

<iframe
  src="https://kuinox.github.io/Plank-Lab/"
  title="Plank benchmark results"
  loading="lazy"
  referrerpolicy="no-referrer"
  style="display:block; width:100%; min-height:52rem; border:1px solid var(--bs-border-color); border-radius:0.375rem; background:white;"
></iframe>
