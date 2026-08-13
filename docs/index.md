# Plank

Plank is a Parquet reader and writer for .NET.

It provides several layers, from rows and datasets down to columns, pages, and file metadata. The higher-level APIs are easier to grasp and handle the Parquet details for you. Use the lower-level APIs when you need control over the file structure.

Declare schemas as C# types. Source generators create type-safe readers and writers, while analyzers report incompatible mappings at build time.

## Fast. Really fast.

Writing Parquet should not be the slow part of your pipeline. These results include the complete in-memory file write, from writer creation to the footer and close.

<link rel="stylesheet" href="benchmarks/write-benchmarks-v10.css">
<div id="write-benchmarks" class="write-benchmarks"
     data-write-results="benchmarks/write-v1.json?v=12"
     data-read-results="benchmarks/read-v1.json?v=5">
  <p class="benchmark-loading" role="status">Loading benchmark results…</p>
</div>
<script src="benchmarks/write-benchmarks-v12.js" defer></script>
