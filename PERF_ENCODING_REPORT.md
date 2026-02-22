# Encoding Performance Report (Updated)

## Benchmark Input

- Date: 2026-02-22
- Machine/runtime: .NET 10.0.3, x64 RyuJIT (`BenchmarkDotNet.Artifacts/Plank.Benchmarks.EncodingMatrixBdnBenchmark-20260222-204938.log`)
- Source CSV: `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingMatrixBdnBenchmark-report.csv`
- Scenarios: 1,000,000 rows, single-column parquet per scenario.

## Latest Optimization Loop (2026-02-22, CET)

Targeted reruns executed during this loop:
- `bool|plain`, `string|delta_byte_array`, `string|delta_length_byte_array`, `string|dictionary`
- command: `dotnet run -c Release --project Plank.Benchmarks/Plank.Benchmarks.csproj -- --filter ... --job short`

Measured `WritePlankAsync` (Job-DARAAU) deltas vs previous matrix in this report:

| Scenario | Previous | Latest | Delta |
| --- | ---: | ---: | ---: |
| `bool|plain` | 1.502 ms | 1.461 ms | -2.7% |
| `string|delta_byte_array` | 14.564 ms | 11.235 ms | -22.9% |
| `string|delta_length_byte_array` | 13.188 ms | 9.090 ms | -31.1% |
| `string|dictionary` | 14.780 ms | 13.31 ms | -10.0% |

Notes:
- `string|delta_byte_array` had multiple reruns (`11.060 ms`, `11.546 ms`, `11.235 ms`) while validating prefix-scan variants; `11.235 ms` is the final state after reverting the slower variant.
- The first dictionary rerun after reverting the hash path measured `12.70 ms`; a subsequent rerun measured `13.31 ms`. Current row uses the most recent run.
- These improvements came from payload batch writes in delta encoders, bool packing/RLE changes, and dictionary-path cleanup.

## Current Matrix (Mean)

| Type | Encoding | Plank | ParquetSharp | Parquet.Net |
| --- | --- | --- | --- | --- |
| bool | plain | 1.315 ms | 2.778 ms | 1.237 ms 🚀 |
| int32 | plain | 1.851 ms 🚀 | 2.295 ms | 15.025 ms |
| int32 | dictionary | 7.354 ms 🚀 | 10.070 ms | 15.326 ms |
| int32 | delta_binary_packed | 1.983 ms 🚀 | 3.057 ms | 14.811 ms |
| int64 | plain | 2.713 ms 🚀 | 3.876 ms | 13.146 ms |
| int64 | dictionary | 21.217 ms | 53.145 ms | 19.902 ms 🚀 |
| int64 | delta_binary_packed | 1.894 ms 🚀 | 3.643 ms | 13.124 ms |
| float | plain | 1.717 ms 🚀 | 2.493 ms | 20.840 ms |
| float | dictionary | 8.375 ms 🚀 | 8.628 ms | 21.097 ms |
| float | byte_stream_split | 2.340 ms 🚀 | 2.904 ms | n/a |
| double | plain | 2.601 ms 🚀 | 3.776 ms | 21.809 ms |
| double | dictionary | 8.420 ms 🚀 | 9.272 ms | 21.538 ms |
| double | byte_stream_split | 3.598 ms 🚀 | 4.568 ms | n/a |
| string | plain | 8.943 ms 🚀 | 26.212 ms | 26.372 ms |
| string | dictionary | 13.856 ms 🚀 | 30.136 ms | 26.047 ms |
| string | delta_length_byte_array | 9.381 ms 🚀 | 26.991 ms | n/a |
| string | delta_byte_array | 11.356 ms 🚀 | 30.736 ms | n/a |

Summary:
- Plank fastest in 15/17 scenarios.
- ParquetSharp fastest in 0/17 scenarios.
- Parquet.Net fastest in 2/17 scenarios (`bool|plain`, `int64|dictionary`).

## Algorithm Reports

### 1. Dictionary Single-Pass Index Capture

Code changes:
- `Plank/Writing/Encoding.cs:85`
- `Plank/Writing/Encoding.cs:90`
- `Plank/Writing/Encoding.cs:101`
- `Plank/Writing/Encoding.cs:127`

What changed:
- Dictionary build and row-index capture are now one pass.
- `int[]` indexes are pooled via `ArrayPool<int>.Shared` and returned in `finally`.
- Removed second dictionary lookup pass.

Measured effect (vs previous baseline in prior report):
- `int64|dictionary`: **27.018 ms -> 4.754 ms** (5.68x faster).
- `int32|dictionary`: **11.610 ms -> 9.649 ms** (1.20x faster).
- `float|dictionary`: **10.902 ms -> 8.935 ms** (1.22x faster).
- `double|dictionary`: **12.244 ms -> 9.402 ms** (1.30x faster).

### 2. Adaptive Dictionary Drop Strategy

Code changes:
- `Plank/Writing/PageStrategy/DefaultStrategy.cs:8`
- `Plank/Writing/PageStrategy/DefaultStrategy.cs:18`
- `Plank/Writing/Encoding.cs:103`
- `Plank/Writing/Encoding.cs:108`
- `Plank/Writing/Encoding.cs:111`

What changed:
- Dictionary mode is now `Maybe` (not always forced).
- Strategy can drop dictionary during build when uniqueness is too high.
- On drop: dictionary page buffers are reset and the page is removed.

Measured effect:
- Main win is the pathological high-cardinality case (`int64|dictionary`) no longer being worst-case.
- Remaining dictionary gap vs ParquetSharp is now small on `int32/float/double` (~3%-6%).

### 3. Byte-Stream-Split Rewrite

Code changes:
- `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs:47`
- `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs:76`
- `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs:113`
- `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs:143`

What changed:
- Reworked to one value pass writing all lanes.
- `float/double` now reinterpret spans (`MemoryMarshal.Cast`) instead of repeated conversions.
- Output is written from one contiguous acquired span.

Measured effect (from previous baseline in prior report):
- `double|byte_stream_split`: **4.898 ms -> 3.655 ms** (1.34x faster).
- `float|byte_stream_split`: now **2.393 ms** (faster than ParquetSharp 2.846 ms in this run).

## Remaining Gaps (Current)

- `bool|plain`: Plank slower than Parquet.Net (1.502 vs 1.191 ms).
- `int32|dictionary`: Plank slightly slower than ParquetSharp (9.649 vs 9.222 ms).
- `float|dictionary`: Plank slightly slower than ParquetSharp (8.935 vs 8.670 ms).
- `double|dictionary`: Plank slightly slower than ParquetSharp (9.402 vs 8.856 ms).

Likely reasons:
- Dictionary path still uses `Dictionary<T,int>` and generic hashing (`Plank/Writing/Encoding.cs:85`).
- No SIMD in dictionary index emission / hash table probing.
- `bool|plain` is fixed-overhead dominated due tiny payload.

## SIMD Investigation

High-value SIMD candidates:
- `Plank/Writing/Encoding/ByteStreamSplitEncoding.cs`
  - SIMD transpose kernel for 16/32 values at a time using AVX2/Vector128 shuffles.
- `Plank/Writing/Encoding/RleBitPackingHybridEncoding.cs`
  - Vectorized bit-pack write path for dictionary indexes.
- `Plank/Writing/Encoding/DeltaBinaryPackedEncoding.cs`
  - SIMD min/max + delta scan for blocks.

Expected ROI:
- Highest: byte-stream-split (already fast, still vectorizable further).
- Medium: dictionary index packing.
- Medium: delta pre-pass (especially large numeric columns).

## Open Follow-Ups

- Implement zero-allocation specialized dictionary tables (remove `Dictionary<T,int>` in hot path).
- Add SIMD kernels with scalar fallback and architecture checks.
- Keep read-matrix benchmark (`ParquetSharpDictionaryReadMatrixBdnBenchmark`) as the policy gate for dictionary drop threshold tuning.
