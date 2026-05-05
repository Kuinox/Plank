# Parquet.Net 6.0.0-pre.8 Encoding Benchmark Report

Date: 2026-05-05

Command for fresh updated run:

```powershell
dotnet run -c Release --project Plank.Benchmarks\Plank.Benchmarks.csproj -- --filter "*EncodingBenchmark*" --join
```

Artifacts:

- Baseline: `benchmarks/report/20260504_parquetnet_5_5_0_baseline/Plank.Benchmarks.EncodingBenchmark-report.csv`
- Updated fresh run: `benchmarks/report/20260505_011239_parquetnet_6_0_0_pre_8_fresh/Plank.Benchmarks.EncodingBenchmark-report.csv`

## Summary

- The current benchmark source is already updated for Parquet.Net 6 APIs. A fresh 5.5.0 rerun was attempted first but no longer builds against this source, so the comparison uses the preserved 5.5.0 baseline artifact.
- Compared across 12 scenarios where both Parquet.Net 5.5.0, Parquet.Net 6.0.0-pre.8, and Plank have valid measurements.
- Parquet.Net 6.0.0-pre.8 is 6.85x faster on arithmetic mean and 2.83x faster on geometric mean versus Parquet.Net 5.5.0.
- Against Plank in the fresh updated run, Parquet.Net wins 8 scenarios and Plank wins 4.
- Biggest Parquet.Net gain: `float/dictionary`, 32.11x faster than 5.5.0.
- Regressions versus 5.5.0: `int64/plain` at 0.71x speed, `string/plain` at 0.76x speed, `double/plain` at 0.78x speed, `int32/plain` at 0.92x speed.

## Results

`PN 6 / Plank` is a time ratio. Below `1.00x` means Parquet.Net is faster than Plank; above `1.00x` means Plank is faster.

| Case | Parquet.Net 5.5.0 | Parquet.Net 6.0.0-pre.8 | PN speedup | Plank | PN 6 / Plank | Winner |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `bool/dictionary` | 446.1 us | 157.0 us | 2.84x | 3.273 ms | 0.05x | Parquet.Net |
| `bool/plain` | 533.8 us | 155.7 us | 3.43x | 1.225 ms | 0.13x | Parquet.Net |
| `double/dictionary` | 42.162 ms | 2.617 ms | 16.11x | 10.654 ms | 0.25x | Parquet.Net |
| `double/plain` | 2.222 ms | 2.841 ms | 0.78x | 2.763 ms | 1.03x | Plank |
| `float/dictionary` | 37.652 ms | 1.173 ms | 32.11x | 10.757 ms | 0.11x | Parquet.Net |
| `float/plain` | 1.064 ms | 958.7 us | 1.11x | 2.229 ms | 0.43x | Parquet.Net |
| `int32/dictionary` | 15.121 ms | 1.137 ms | 13.30x | 6.675 ms | 0.17x | Parquet.Net |
| `int32/plain` | 718.8 us | 779.6 us | 0.92x | 1.928 ms | 0.40x | Parquet.Net |
| `int64/dictionary` | 23.500 ms | 2.593 ms | 9.06x | 7.368 ms | 0.35x | Parquet.Net |
| `int64/plain` | 1.934 ms | 2.734 ms | 0.71x | 1.970 ms | 1.39x | Plank |
| `string/dictionary` | 30.851 ms | 30.377 ms | 1.02x | 24.777 ms | 1.23x | Plank |
| `string/plain` | 26.783 ms | 35.067 ms | 0.76x | 16.313 ms | 2.15x | Plank |

## Notes

- The largest gains are still concentrated in Parquet.Net dictionary encoding for primitive numeric types.
- Plank remains faster in the delta-binary-packed integer cases and string cases in this fresh run.
- BenchmarkDotNet reported expected issue rows for unsupported Parquet.Net encoding/type pairs and the existing ParquetSharp NU1902 vulnerability warning during restore/build.
