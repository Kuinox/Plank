# Encoding All Combinations Benchmark Results

Source reports:
- Plank numbers: `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingBenchmark-report-github.md`
- ParquetSharp / Parquet.Net numbers: `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingMatrixBdnBenchmark-report-github.md`

## Environment

- BenchmarkDotNet `0.15.6`
- OS: Linux CachyOS
- CPU: AMD Ryzen 9 7900X (12C/24T)
- Runtime: .NET `10.0.3`
- Rows: `1,000,000`

> **Plank numbers reflect the rewritten `ReusableDictionaryState<T>` (packed ultra-sparse hash table
> + wyhash + Murmur3 finalizer for float/double). Previous Plank numbers are shown in the diff columns where relevant.**

## Executive Summary

- On commonly valid parquet type/encoding pairs, **Plank is fastest in 14/18 cases**.
- **Parquet.Net is fastest in 4/18 cases** (bool/plain, bool/dictionary, float/byte-stream-split, double/byte-stream-split).
- **ParquetSharp wins 0/18** — float/dictionary moved to Plank after adding Murmur3 finalizer for float hashing.
- Plank is **zero-allocation** across all paths (plain, delta, dictionary).
- ParquetSharp allocates 1–50 MB/op depending on type. Parquet.Net allocates 5 KB–50 MB/op.

## Winner Count

| Library      | Wins (18 valid pairs) |
|--------------|-----------------------:|
| **Plank**    | **14** (was 11)        |
| Parquet.Net  | 4                      |
| ParquetSharp | 0 (was 3)              |

*Dictionary rewrite moved int32/dictionary and double/dictionary from ParquetSharp wins to Plank wins.
Murmur3 finalizer for float hashing moved float/dictionary from ParquetSharp to Plank (was 16% slower, now 10% faster).*

## Throughput (Mean µs, lower is better)

| Type   | Encoding                 |  Plank   | ParquetSharp |  Parquet.Net | Fastest        |
|--------|--------------------------|--------:|-------------:|-------------:|----------------|
| bool   | plain                    |   518.6 |     2,050.7  |      467.9   | Parquet.Net 🚀 |
| bool   | dictionary               | 4,231.3 |     2,050.8  |      419.5   | Parquet.Net 🚀 |
| int32  | plain                    |   309.9 |       905.9  |      596.6   | Plank 🚀       |
| int32  | delta_binary_packed      | 1,214.9 |     2,386.4  |    2,576.0   | Plank 🚀       |
| int32  | dictionary               | 4,546.0 |     7,842.4  |   13,366.5   | **Plank 🚀** ¹ |
| int64  | plain                    |   491.5 |     2,304.4  |      995.2   | Plank 🚀       |
| int64  | delta_binary_packed      | 1,209.4 |     2,936.3  |    2,624.6   | Plank 🚀       |
| int64  | dictionary               | 4,687.4 |    46,372.0  |   18,034.6   | Plank 🚀       |
| float  | plain                    |   311.0 |     1,263.4  |      631.3   | Plank 🚀       |
| float  | byte_stream_split        | 1,045.7 |     1,814.6  |      618.1   | Parquet.Net 🚀 |
| float  | dictionary               | 6,719.0 |     7,442.5  |   20,084.8   | **Plank 🚀** ² |
| double | plain                    |   501.4 |     2,312.2  |    1,103.0   | Plank 🚀       |
| double | byte_stream_split        | 1,976.6 |     3,194.7  |    1,226.4   | Parquet.Net 🚀 |
| double | dictionary               | 7,064.0 |     8,056.5  |   21,419.1   | **Plank 🚀** ¹ |
| string | plain                    | 6,469.2 |    22,126.9  |   23,881.4   | Plank 🚀       |
| string | delta_length_byte_array  | 6,938.8 |    23,870.6  |   23,502.1   | Plank 🚀       |
| string | delta_byte_array         | 9,780.4 |    28,807.7  |   24,235.8   | Plank 🚀       |
| string | dictionary               |12,883.1 |    30,230.0  |   23,809.7   | Plank 🚀       |

¹ Was previously a ParquetSharp win; new Plank dictionary implementation reversed the result.
² Was previously a ParquetSharp win (16% lead); Murmur3 finalizer for float hashing fixed catastrophic
  hash clustering (`float.GetHashCode()` = raw IEEE 754 bits → all `(i%10000)/3f` values share the same
  lower 16 mantissa bits → only 48/65536 slots used). Now Plank is 10% faster than ParquetSharp.

## Allocation (bytes/op)

| Type   | Encoding                |     Plank | ParquetSharp |   Parquet.Net |
|--------|-------------------------|----------:|-------------:|--------------:|
| bool   | plain                   |       0 B |    167,752 B |       5,248 B |
| bool   | dictionary              |       0 B |    167,752 B |       5,248 B |
| int32  | plain                   |       0 B |  4,055,933 B |       6,816 B |
| int32  | delta_binary_packed     |       0 B |     97,056 B |     762,832 B |
| int32  | dictionary              |       0 B |  2,582,800 B |  42,265,671 B |
| int64  | plain                   |       0 B |  8,072,838 B |       8,056 B |
| int64  | delta_binary_packed     |       0 B |     91,400 B |     755,920 B |
| int64  | dictionary              |       0 B | 10,521,680 B |  49,867,648 B |
| float  | plain                   |       0 B |  4,055,482 B |       6,736 B |
| float  | byte_stream_split       |       0 B |  4,055,532 B |       6,656 B |
| float  | dictionary              |       0 B |  1,847,240 B |  41,904,316 B |
| double | plain                   |       0 B |  8,072,409 B |       7,896 B |
| double | byte_stream_split       |       0 B |  8,072,480 B |       7,896 B |
| double | dictionary              |       0 B |  1,903,656 B |  41,944,316 B |
| string | plain                   |       0 B | 19,758,896 B |  44,041,768 B |
| string | delta_length_byte_array |       0 B | 15,818,976 B |  44,041,768 B |
| string | delta_byte_array        |       0 B | 10,080,400 B |  44,041,768 B |
| string | dictionary              |       0 B |  9,700,600 B |  35,558,814 B |

## Dictionary Encoding: Plank Before vs After

| Type   | Old Plank   | New Plank   | Speedup | vs ParquetSharp (new) |
|--------|------------:|------------:|--------:|-----------------------:|
| bool   |   8,819.9 μs|   4,231.3 μs| 2.08×   | 2.06× slower           |
| int32  |  10,080.5 μs|   4,546.0 μs| 2.22×   | 1.72× faster           |
| int64  |   5,419.8 μs|   4,687.4 μs| 1.16×   | 9.89× faster           |
| float  |  21,457.2 μs|   6,719.0 μs| 3.19×   | 1.11× faster           |
| double |  14,373.6 μs|   7,064.0 μs| 2.03×   | 1.14× faster           |
| string |  19,011.3 μs|  12,883.1 μs| 1.48×   | 2.35× faster           |

## Notes

- `NA` cells exist in the full matrix where encoding/type pair is unsupported (e.g. bool can't use delta_binary_packed).
- bool/dictionary is unusually slow in Plank because the sorted fast-path treats a column of `true/false` as two-valued and still goes through `GetOrAddIndex` once the order breaks.
- `delta_length_byte_array` appears truncated as `delta(...)array [23]` in BDN markdown output.
- Results are from one hardware/runtime configuration; treat as comparative, not absolute.
- Plank allocation is 0 across all paths: touched-slot Reset avoids full table clear; no GC pressure in steady state.
- float/double hashing uses a Murmur3 finalizer on the raw IEEE 754 bits instead of `GetHashCode()`. For structured float data (e.g. `k/3f`), `GetHashCode()` = raw bits causes catastrophic mantissa clustering (only 48/65536 slots used); the finalizer eliminates this and also improves double (2.03× total speedup vs old Plank).
