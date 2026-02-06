# Plank

Minimal Parquet writer under construction.

## Notes

- Columns can be pre-serialized ahead of row-group writing with `ParquetWriter.SerializeColumn(...)`, then written later via `RowGroupWriter.WriteAsync(serializedColumn)`.
- Column encode buffers are rented from a named writer pool (`NamedMemoryPool`) using bucket keys such as `column:Int32:4096`, so similar buffer shapes can share reusable storage.
- Current write path supports splitting fixed-width required columns into multiple data pages per chunk.
- Repeated primitive columns are supported via `RowGroupWriter.WriteAsync(column, new RepeatedValues<T>(rows))` and include DataPageV2 repetition/definition levels.
- Future work: support multiple data pages per column chunk for streaming and page sizing.
- Writer compression is configurable via `ParquetWriterOptions.Compression` (`None`, `Gzip`, `Brotli` currently implemented).
- Page splitting can be tuned with `RowGroupOptions.MaxPageValueCount` and `RowGroupOptions.MaxPageBytes` (currently applied to fixed-width required columns and required byte-array columns, with soft overflow to keep value boundaries intact).

## Benchmarks

- `Plank.Benchmarks` is a dedicated BenchmarkDotNet project that downloads NYC TLC yellow taxi parquet files, loads vendor IDs in memory, and benchmarks writing with Plank.
- Run:
  - `dotnet run -c Release --project .\Plank.Benchmarks\Plank.Benchmarks.csproj -- --file-count 3`
  - Optional data directory override: `--data-dir C:\path\to\nyc-data`
