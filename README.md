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


## TODO:

we have a leak currently in the dict since we dont completly reset the dicts.

  ---                                                                                                                              
  Why Parquet.NET Is Faster — 4 Cases                                                                                              
                                                                                                                                   
  1. bool/dictionary — 10× slower (critical)                                                                                       
                                                                                                                                   
  The most dramatic gap. The sorted fast-path kills performance for bools:                                                         
                                                                                                                                   
  - Row 0: true → sorted index 0
  - Row 1: false → sorted, index 1
  - Row 2: true → order breaks → EnableMap() called → full hash table enabled
  - Rows 3–1,000,000: all go through GetOrAddIndex() with hash lookups for a 2-valued type

  The fix is a bool-specific fast path that skips the hash table entirely — just emit value ? 1 : 0 directly as the index.

  ---
  2. float/byte_stream_split and double/byte_stream_split — 1.6–1.7× slower

  The current loop writes bytes in this pattern per iteration:
  lane0[i]  → offset i          (byte 0 region)
  lane1[i]  → offset 1M + i     (byte 1 region)
  lane2[i]  → offset 2M + i     (byte 2 region)
  lane3[i]  → offset 3M + i     (byte 3 region)

  For 1M floats that's 4 stores 1MB apart per iteration — cache thrash. Every iteration touches 4 different cache lines.

  The fix is to invert the loop nesting: outer loop over the 4 byte lanes, inner loop over values. Then each lane writes
  sequentially and the CPU can prefetch properly.

  ---
  3. bool/plain — 10% slower

  The current packing loop does 8 conditional OR-chains per byte. Minor SIMD opportunity, but low priority given only a 10% gap.

  ---
  Priority Summary


delta_binary_packed	 is uninmplemented.