# Encoding Performance Analysis Report

## Scope And Inputs

Analyzed files:
- `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingMatrixBdnBenchmark-report.csv`
- `/tmp/plank_single_column_mean_table.md`
- Benchmark setup and scenario definitions in `Plank.Benchmarks/EncodingMatrixBdnBenchmark.cs:46`, `Plank.Benchmarks/EncodingMatrixBdnBenchmark.cs:258`, and `Plank.Benchmarks/SingleColumnScenarioCatalog.cs:40`
- Current Plank encoding paths under `Plank/Writing/Encoding/*`

Benchmark dataset characteristics (1,000,000 rows each) from `Plank.Benchmarks/EncodingMatrixBdnBenchmark.cs:56`:
- `bool`: alternating `true/false`
- `int32`: `i % 100_000` (100k-cardinality repeating)
- `int64`: `i * 37` (strictly increasing, effectively all unique)
- `float`: `(i % 10_000) / 3f` (10k-cardinality repeating)
- `double`: `(i % 10_000) / 7d` (10k-cardinality repeating)
- `string`: `"val-{i % 2048}"` (2,048-cardinality repeating, shared prefix)

## High-Level Results

From the CSV and mean table:
- Plank is fastest in **11/17** scenarios.
- ParquetSharp is fastest in **5/17** scenarios.
- Parquet.Net is fastest in **1/17** scenario (`bool|plain`).

Cases where Plank is slower than ParquetSharp:
- `int64|dictionary`: 27.018 ms vs 9.137 ms (2.96x slower)
- `double|dictionary`: 12.244 ms vs 9.124 ms (1.34x slower)
- `float|dictionary`: 10.902 ms vs 8.499 ms (1.28x slower)
- `int32|dictionary`: 11.610 ms vs 9.485 ms (1.22x slower)
- `double|byte_stream_split`: 4.898 ms vs 4.494 ms (1.09x slower)

Cases where Plank is slower than Parquet.Net:
- `int64|dictionary`: 27.018 ms vs 12.669 ms
- `bool|plain`: 1.536 ms vs 1.120 ms

## Why Plank Is Faster In Many Encodings

### 1. Plain fixed-width writes are direct and branch-light

Code path:
- `Plank/Writing/Encoding/PlainEncoding.cs:66`
- `Plank/Writing/Encoding/PlainEncoding.cs:88`
- `Plank/Writing/Encoding/PlainEncoding.cs:110`
- `Plank/Writing/Encoding/PlainEncoding.cs:133`

For numeric plain encodings, Plank writes directly from typed spans into destination buffers (`MemoryMarshal.AsBytes(...).CopyTo(...)`) on little-endian systems. That aligns with results where Plank beats ParquetSharp on plain `int32/int64/float/double` and strongly beats Parquet.Net on those same cases.

### 2. Delta binary packed path matches the test data well

Code path:
- `Plank/Writing/Encoding/DeltaBinaryPackedEncoding.cs:29`
- `Plank/Writing/Encoding/DeltaBinaryPackedEncoding.cs:70`
- `Plank/Writing/Encoding/DeltaBinaryPackedEncoding.cs:133`

For `int32|delta_binary_packed` and `int64|delta_binary_packed`, Plank is fastest vs both libraries. The benchmark data is highly delta-friendly (especially monotonic `int64 = i * 37`), and Plank’s implementation is single-pass over blocks with low per-value overhead.

### 3. String plain/delta paths avoid extra framework overhead

Code path:
- `Plank/Writing/Encoding/PlainEncoding.cs:156`
- `Plank/Writing/Encoding/DeltaLengthByteArrayEncoding.cs:23`
- `Plank/Writing/Encoding/DeltaByteArrayEncoding.cs:23`

Plank is substantially faster than ParquetSharp on string plain and delta-byte-array variants. The implementation is straightforward span/array processing and writes contiguous payloads directly to `BufferWriter`, which matches the strong throughput lead in these string scenarios.

## Why Plank Is Slower In Specific Encodings

### 1. Dictionary path is always forced when requested, with no fallback

Code path:
- `Plank/Writing/PageStrategy/DefaultStrategy.cs:8`
- `Plank/Writing/PageStrategy/DefaultStrategy.cs:18`
- `Plank/Writing/Encoding.cs:58`
- `Plank/Writing/Encoding.cs:80`

`DefaultStrategy` returns `DictionaryMode.Forced` whenever dictionary encoding is present and never drops it. There is no adaptive fallback for high-cardinality columns.

Impact is severe for `int64|dictionary`:
- Plank emitted bytes: **10,500,057**
- ParquetSharp emitted bytes: **8,279,008**
- Parquet.Net emitted bytes: **32,315**

Given `int64` test data is essentially unique, forced dictionary is pathological for Plank in both time and size.

### 2. Plank dictionary encoding does two hash-lookup passes + large temporary buffers

Code path:
- Build dictionary: `Plank/Writing/Encoding.cs:67`, `Plank/Writing/Encoding.cs:72`
- Second pass to map indexes: `Plank/Writing/Encoding.cs:146`
- Pooled index array rent/return: `Plank/Writing/Encoding.cs:141`, `Plank/Writing/Encoding.cs:161`
- Index encoding: `Plank/Writing/Encoding/PlainDictionaryEncoding.cs:5`, `Plank/Writing/Encoding/RleBitPackingHybridEncoding.cs:16`

Dictionary scenarios currently pay:
- First pass to discover uniques.
- Second pass to re-lookup each row for index emission.
- Large temporary `int[]` indexes for full-page writes.

This correlates with higher allocations and slower runtimes in all numeric dictionary cases vs ParquetSharp.

### 3. Byte-stream split `double` path is scalar lane-by-lane and likely not vectorized

Code path:
- `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs:95`
- inner loops at `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs:108`

Implementation performs nested loops over lanes and values using repeated `BitConverter.DoubleToInt64Bits(...)`. It is correct, but scalar and compute-heavy. This likely explains why Plank is slightly behind ParquetSharp on `double|byte_stream_split` while near parity on `float|byte_stream_split`.

### 4. Bool plain is small-payload dominated, where fixed overhead matters

Code path:
- bool bit-pack: `Plank/Writing/Encoding/PlainEncoding.cs:45`
- page/metadata processing: `Plank/Writing/RowGroupWriter.cs:53`

`bool|plain` payload is only ~125 KB, so fixed pipeline overhead is a larger fraction of total time. Plank is close but behind Parquet.Net in this one case.

## Comparison Vs ParquetSharp And Parquet.Net (Using Observed Patterns)

## ParquetSharp

Evidence from output patterns:
- For `int32/float/double|string` dictionary scenarios, emitted sizes are very close to Plank (same general encoding choice), but ParquetSharp is faster.
- For `int64|dictionary`, ParquetSharp output (`8,279,008`) is much closer to plain (`8,000,198`) than to Plank’s forced-dictionary output (`10,500,057`).

Likely behavior:
- ParquetSharp appears to have more optimized dictionary internals and/or adaptive fallback heuristics for bad dictionary cardinality (especially `int64` unique-like data).

## Parquet.Net

Evidence from output patterns:
- `int64` scenarios (`plain`, `dictionary`, `delta_binary_packed`) all emit **32,315** bytes.
- `int32` scenarios (`plain`, `dictionary`, `delta_binary_packed`) all emit **4,400,077** bytes.
- `float` plain and dictionary emit identical size (**3,040,077**), same for `double` and `string`.

Likely behavior (inference):
- Requested encoding flags in `BuildParquetNetOptions` (`Plank.Benchmarks/EncodingMatrixBdnBenchmark.cs:276`) do not appear to produce distinctly different physical outputs for several types in this benchmark.
- Parquet.Net may apply internal/default encoding choices independent of requested option combinations for these scenarios.

Note on confidence:
- CSV includes scenario labels and emitted byte sizes, but not explicit per-file encoding metadata lists. The Parquet.Net and ParquetSharp behavior above is inferred from strong repeated size patterns.

## Prioritized Fix Plan (No Implementation Yet)

### P0: Add adaptive dictionary fallback and “maybe” mode

Why first:
- Largest regression source by far (`int64|dictionary`), biggest speed and size win potential.

Plan:
- Implement a strategy that can return `DictionaryMode.Maybe` (currently only forced/disabled behavior is active in default strategy: `Plank/Writing/PageStrategy/DefaultStrategy.cs:8`).
- Use early-sample heuristics in `TryWriteDictionaryPage(...)` (`Plank/Writing/Encoding.cs:54`) to disable dictionary when projected dictionary+index bytes exceed projected plain bytes.
- Allow fallback when unique ratio crosses threshold.

Expected impact:
- Remove pathological int64 dictionary behavior.
- Likely improve other numeric dictionary cases where dictionary cost outweighs benefits.

### P1: Eliminate second dictionary lookup pass and reduce temporary allocations

Why next:
- Affects every dictionary scenario and explains consistent Plank-vs-ParquetSharp gap.

Plan:
- During dictionary build, capture row indexes once instead of recomputing via second `TryGetValue` loop.
- Reuse pooled index buffers across encode calls where possible.
- Replace generic `Dictionary<T,int>` path with lower-overhead specialized tables for primitives and byte arrays.

Relevant current code:
- `Plank/Writing/Encoding.cs:67`
- `Plank/Writing/Encoding.cs:146`
- `Plank/Writing/Encoding.cs:141`

### P2: Optimize byte-stream split for double with vectorized/transposed kernels

Why:
- Isolated remaining non-dictionary regression vs ParquetSharp.

Plan:
- Replace scalar lane/value loop with SIMD-friendly transpose strategy for 64-bit lanes.
- Reduce per-element `BitConverter` overhead by reinterpreting spans when possible.

Relevant current code:
- `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs:95`

### P3: Lower fixed overhead on small payloads (bool plain)

Why:
- Small regression only, but easy follow-up once major issues are solved.

Plan:
- Fast-path single-page, tiny-column writes to minimize header/metadata churn.
- Re-check bool packing loop and possible branchless improvements.

Relevant current code:
- `Plank/Writing/Encoding/PlainEncoding.cs:45`
- `Plank/Writing/RowGroupWriter.cs:68`

### P4: Improve benchmark diagnostics to include emitted encoding metadata

Why:
- Current CSV provides size signals but not explicit encoding lists for competitor files.

Plan:
- Extend benchmark metric capture to record `ColumnChunk` encoding metadata (not only sizes) alongside current `ColumnCompressedBytes`/`ColumnUncompressedBytes`.

Relevant current setup:
- `Plank.Benchmarks/EncodingMatrixBdnBenchmark.cs:224`
- `Plank.Benchmarks/EncodingBenchmarkMetrics.cs:9`

## Key Takeaways

- Plank already leads most scenarios, especially plain numeric, delta binary packed, and string encodings.
- Current biggest issue is dictionary policy/implementation, not the non-dictionary core encoders.
- Fixing adaptive dictionary fallback plus reducing dictionary lookup/allocation overhead should deliver the highest ROI and likely close most remaining gaps against ParquetSharp.
