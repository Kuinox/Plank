# Encoding All Combinations Benchmark Results

Source report:
- `Plank.Benchmarks/BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingAllCombinationsBdnBenchmark-report.csv`
- `Plank.Benchmarks/BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingAllCombinationsBdnBenchmark-report-github.md`

## Environment

- BenchmarkDotNet `0.15.6`
- OS: Linux CachyOS
- CPU: AMD Ryzen 9 7900X (12C/24T)
- Runtime: .NET `10.0.3`
- Rows: `1,000,000`

## Executive Summary

- On commonly valid parquet type/encoding pairs, **Plank is fastest in 11/18 cases**.
- **Parquet.Net is fastest in 4/18 cases** (notably bool and byte-stream-split for float/double).
- **ParquetSharp is fastest in 3/18 cases** (int32/float/double dictionary).
- Plank plain/delta paths are near-zero allocation (`~152 B`) in the benchmark harness.
- Dictionary paths in Plank currently allocate heavily compared to plain/delta paths.
- Many matrix cells are `NA` because the combination is not supported/valid for that type+encoding.

## Winner Count (18 representative valid pairs)

| Library | Wins |
|---|---:|
| Plank | 11 |
| Parquet.Net | 4 |
| ParquetSharp | 3 |

## Throughput (Mean, lower is better)

| Type | Encoding | Plank | ParquetSharp | Parquet.Net | Fastest |
|---|---|---:|---:|---:|---|
| bool | plain | 508.2 us | 2050.7 us | 467.9 us | Parquet.Net 🚀 |
| bool | dictionary | 8819.9 us | 2050.8 us | 419.5 us | Parquet.Net 🚀 |
| int32 | plain | 317.3 us | 905.9 us | 596.6 us | Plank 🚀 |
| int32 | delta_binary_packed | 1223.1 us | 2386.4 us | 2576.0 us | Plank 🚀 |
| int32 | dictionary | 10080.5 us | 7842.4 us | 13366.5 us | ParquetSharp 🚀 |
| int64 | plain | 451.4 us | 2304.4 us | 995.2 us | Plank 🚀 |
| int64 | delta_binary_packed | 1157.3 us | 2936.3 us | 2624.6 us | Plank 🚀 |
| int64 | dictionary | 5419.8 us | 46372.0 us | 18034.6 us | Plank 🚀 |
| float | plain | 316.4 us | 1263.4 us | 631.3 us | Plank 🚀 |
| float | byte_stream_split | 1023.6 us | 1814.6 us | 618.1 us | Parquet.Net 🚀 |
| float | dictionary | 21457.2 us | 7442.5 us | 20084.8 us | ParquetSharp 🚀 |
| double | plain | 449.4 us | 2312.2 us | 1103.0 us | Plank 🚀 |
| double | byte_stream_split | 1867.1 us | 3194.7 us | 1226.4 us | Parquet.Net 🚀 |
| double | dictionary | 14373.6 us | 8056.5 us | 21419.1 us | ParquetSharp 🚀 |
| string | plain | 6337.5 us | 22126.9 us | 23881.4 us | Plank 🚀 |
| string | delta_length_byte_array* | 6883.4 us | 23870.6 us | 23502.1 us | Plank 🚀 |
| string | delta_byte_array | 9722.3 us | 28807.7 us | 24235.8 us | Plank 🚀 |
| string | dictionary | 19011.3 us | 30230.0 us | 23809.7 us | Plank 🚀 |

\* In BDN markdown this appears truncated as `delta(...)array [23]`.

## Allocation Snapshot (bytes/op)

| Type | Encoding | Plank | ParquetSharp | Parquet.Net |
|---|---|---:|---:|---:|
| bool | plain | 0 B | 167,752 B | 5,248 B |
| bool | dictionary | 0 B | 167,752 B | 5,248 B |
| int32 | plain | 0 B | 4,055,933 B | 6,816 B |
| int32 | delta_binary_packed | 0 B | 97,056 B | 762,832 B |
| int32 | dictionary | 0 B | 2,582,800 B | 42,265,671 B |
| int64 | plain | 0 B | 8,072,838 B | 8,056 B |
| int64 | delta_binary_packed | 0 B | 91,400 B | 755,920 B |
| int64 | dictionary | 0 B | 10,521,680 B | 49,867,648 B |
| float | plain | 0 B | 4,055,482 B | 6,736 B |
| float | byte_stream_split | 0 B | 4,055,532 B | 6,656 B |
| float | dictionary | 0 B | 1,847,240 B | 41,904,316 B |
| double | plain | 0 B | 8,072,409 B | 7,896 B |
| double | byte_stream_split | 0 B | 8,072,480 B | 7,896 B |
| double | dictionary | 0 B | 1,903,656 B | 41,944,316 B |
| string | plain | 0 B | 19,758,896 B | 44,041,768 B |
| string | delta_length_byte_array* | 0 B | 15,818,976 B | 44,041,768 B |
| string | delta_byte_array | 0 B | 10,080,400 B | 44,041,768 B |
| string | dictionary | 0 B | 9,700,600 B | 35,558,814 B |

## Notes

- Plank throughput values in the table above were refreshed from `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingBenchmark-report.csv`.
- Plank allocation values in the table above were refreshed from `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingBenchmark-report.csv` (`0 B` for those measured pairs).
- Results are from one hardware/runtime configuration and should be treated as comparative, not absolute.
- `NA` cells exist in the full matrix where the encoding/type pair is unsupported or not measured.
