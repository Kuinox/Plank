# Parquet.Net 6.0.0-pre.8 Clean Encoding Benchmark Report

Date: 2026-05-05

Command:

```powershell
dotnet run -c Release --project Plank.Benchmarks\Plank.Benchmarks.csproj -- --filter "*EncodingBenchmark*" --join
```

Artifacts:

- Baseline: `benchmarks/report/20260504_parquetnet_5_5_0_baseline/Plank.Benchmarks.EncodingBenchmark-report.csv`
- Clean run: `benchmarks/report/20260505_021101_parquetnet_6_0_0_pre_8_clean/Plank.Benchmarks.EncodingBenchmark-report.csv`

## Summary

- ParquetSharp is no longer part of the timed encoding benchmark.
- Parquet.Net rows that do not produce the requested encoding now export as `NA`; numeric dictionary and unsupported byte-stream/delta-string cases are excluded from speed comparisons.
- Compared across 9 valid Parquet.Net scenarios.
- Parquet.Net 6.0.0-pre.8 is 1.01x faster on arithmetic mean and 0.87x faster on geometric mean versus Parquet.Net 5.5.0 for valid scenarios only.
- Against Plank in this clean run, Parquet.Net wins 3 scenarios and Plank wins 6.
- Biggest valid Parquet.Net gain versus 5.5.0: `bool/plain`, 3.03x faster.
- Regressions versus 5.5.0: `int32/plain` at 0.50x speed, `double/plain` at 0.62x speed, `string/plain` at 0.67x speed, `float/plain` at 0.78x speed, `int64/delta_binary_packed` at 0.78x speed, `int64/plain` at 0.82x speed, `int32/delta_binary_packed` at 0.90x speed, `string/dictionary` at 0.97x speed.
- Parquet.Net exported `NA` for 11 unsupported or non-honored encoding cases: `bool/dictionary`, `double/byte_stream_split`, `double/dictionary`, `float/byte_stream_split`, `float/dictionary`, `int32/byte_stream_split`, `int32/dictionary`, `int64/byte_stream_split`, `int64/dictionary`, `string/delta_byte_array`, `string/delta_length_byte_array`.

## Results

`PN 6 / Plank` is a time ratio. Below `1.00x` means Parquet.Net is faster than Plank; above `1.00x` means Plank is faster.

| Case | Parquet.Net 5.5.0 | Parquet.Net 6.0.0-pre.8 | PN speedup | Plank | PN 6 / Plank | Winner |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `bool/plain` | 533.8 us | 175.9 us | 3.03x | 1.342 ms | 0.13x | Parquet.Net |
| `double/plain` | 2.222 ms | 3.568 ms | 0.62x | 3.274 ms | 1.09x | Plank |
| `float/plain` | 1.064 ms | 1.368 ms | 0.78x | 2.501 ms | 0.55x | Parquet.Net |
| `int32/delta_binary_packed` | 3.158 ms | 3.525 ms | 0.90x | 2.898 ms | 1.22x | Plank |
| `int32/plain` | 718.8 us | 1.427 ms | 0.50x | 1.988 ms | 0.72x | Parquet.Net |
| `int64/delta_binary_packed` | 3.164 ms | 4.057 ms | 0.78x | 2.355 ms | 1.72x | Plank |
| `int64/plain` | 1.934 ms | 2.356 ms | 0.82x | 2.124 ms | 1.11x | Plank |
| `string/dictionary` | 30.851 ms | 31.722 ms | 0.97x | 26.569 ms | 1.19x | Plank |
| `string/plain` | 26.783 ms | 39.774 ms | 0.67x | 20.771 ms | 1.91x | Plank |

## Unsupported/Excluded Parquet.Net Rows

These are deliberately `NA` because Parquet.Net either cannot emit that encoding through the benchmark API or would silently fall back to another encoding.

| Case | Plank | Parquet.Net |
| --- | ---: | ---: |
| `bool/dictionary` | 3.646 ms | NA |
| `double/byte_stream_split` | 5.992 ms | NA |
| `double/dictionary` | 12.371 ms | NA |
| `float/byte_stream_split` | 3.808 ms | NA |
| `float/dictionary` | 11.668 ms | NA |
| `int32/byte_stream_split` | 2.952 ms | NA |
| `int32/dictionary` | 7.880 ms | NA |
| `int64/byte_stream_split` | 4.765 ms | NA |
| `int64/dictionary` | 7.217 ms | NA |
| `string/delta_byte_array` | 27.153 ms | NA |
| `string/delta_length_byte_array` | 21.846 ms | NA |

## Notes

- The old headline speedup was inflated by numeric dictionary rows where Parquet.Net wrote plain encoding. Those rows are now excluded.
- In valid apples-to-apples rows, Parquet.Net is faster for `bool/plain`, `float/plain`, and `int32/plain`; Plank is faster for both int delta cases, `double/plain`, `int64/plain`, and string cases.
- BenchmarkDotNet still reports issue rows for deliberate `NotSupportedException` cases, which is expected.
