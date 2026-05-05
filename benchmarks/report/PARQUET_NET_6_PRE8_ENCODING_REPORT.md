# Parquet.Net 6.0.0-pre.8 Encoding Benchmark Report

Date: 2026-05-04

Command used for both runs:

```powershell
dotnet run -c Release --project Plank.Benchmarks\Plank.Benchmarks.csproj -- --filter "*EncodingBenchmark*" --join
```

Artifacts:

- Baseline: `benchmarks/report/20260504_parquetnet_5_5_0_baseline/Plank.Benchmarks.EncodingBenchmark-report.csv`
- Updated: `benchmarks/report/20260504_parquetnet_6_0_0_pre_8/Plank.Benchmarks.EncodingBenchmark-report.csv`

Only scenarios with both Parquet.Net and Plank results are included. Unsupported type/encoding pairs remain `NA` in the raw BenchmarkDotNet reports.

## Summary

- Parquet.Net package changed from `5.5.0` to `6.0.0-pre.8`.
- Compared across 14 supported Parquet.Net scenarios.
- Parquet.Net 6.0.0-pre.8 is 7.82x faster on arithmetic mean, 2.86x faster on geometric mean, versus Parquet.Net 5.5.0.
- Against Plank in the updated run, Parquet.Net wins 9 scenarios and Plank wins 5.
- Biggest Parquet.Net gain: `float/dictionary`, 43.79x faster than 5.5.0.
- Regressions: `string/plain` is 0.83x of 5.5.0 speed, and `int64/plain` is 0.97x.

## Results

`PN 6 / Plank` below is time ratio. Below 1.00x means Parquet.Net is faster than Plank; above 1.00x means Plank is faster.

| Type | Encoding | Parquet.Net 5.5.0 | Parquet.Net 6.0.0-pre.8 | PN speedup | Plank | PN 6 / Plank | Winner |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| bool | plain | 533.8 us | 143.4 us | 3.72x | 1,146.7 us | 0.13x | Parquet.Net |
| bool | dictionary | 446.1 us | 145.5 us | 3.07x | 3,089.6 us | 0.05x | Parquet.Net |
| int32 | plain | 718.8 us | 709.5 us | 1.01x | 1,820.4 us | 0.39x | Parquet.Net |
| int32 | dictionary | 15,121.4 us | 779.6 us | 19.40x | 6,449.0 us | 0.12x | Parquet.Net |
| int32 | delta_binary_packed | 3,157.6 us | 3,128.2 us | 1.01x | 2,206.1 us | 1.42x | Plank |
| int64 | plain | 1,934.4 us | 1,996.5 us | 0.97x | 1,765.1 us | 1.13x | Plank |
| int64 | dictionary | 23,499.9 us | 2,250.4 us | 10.44x | 6,655.5 us | 0.34x | Parquet.Net |
| int64 | delta_binary_packed | 3,163.5 us | 2,997.8 us | 1.06x | 1,931.6 us | 1.55x | Plank |
| float | plain | 1,063.6 us | 921.6 us | 1.15x | 2,122.1 us | 0.43x | Parquet.Net |
| float | dictionary | 37,652.1 us | 859.8 us | 43.79x | 9,106.5 us | 0.09x | Parquet.Net |
| double | plain | 2,222.1 us | 2,080.7 us | 1.07x | 2,510.3 us | 0.83x | Parquet.Net |
| double | dictionary | 42,161.5 us | 2,023.5 us | 20.84x | 10,003.7 us | 0.20x | Parquet.Net |
| string | plain | 26,782.6 us | 32,099.8 us | 0.83x | 16,929.1 us | 1.90x | Plank |
| string | dictionary | 30,850.7 us | 28,140.7 us | 1.10x | 22,610.9 us | 1.24x | Plank |

## Notes

- The large Parquet.Net improvement is concentrated in dictionary encoding for primitive numeric types.
- Plank remains faster for `int32/delta_binary_packed`, `int64/plain`, `int64/delta_binary_packed`, `string/plain`, and `string/dictionary`.
- BenchmarkDotNet reported expected issue rows for unsupported encoding/type pairs and the existing ParquetSharp vulnerability warning during restore/build.
