# Benchmark Comparison (Plank vs ParquetSharp vs Parquet.Net)

Date: 2026-02-24T00:44:59+01:00  
Host: `Linux kuinox-desktop 6.19.2-2-cachyos x86_64`  
.NET SDK: `10.0.103`

## Library Selection (evidence from repo)
Primary library: **Plank** (repo name/project and benchmark targets).  
Competing libraries selected: **ParquetSharp** and **Parquet.Net**.

Evidence:
- `Plank.Benchmarks/Plank.Benchmarks.csproj` references `ParquetSharp` and `Parquet.Net`.

## Commands Run
```bash
cd /home/kuinox/dev/Plank

date -Iseconds
uname -a
dotnet --info | sed -n '1,80p'

dotnet run -c Release --project Plank.Benchmarks/Plank.Benchmarks.csproj -- --quick-perf 1000000 \
  | tee benchmarks/report/bench_comparison_quick_perf_20260224.txt

dotnet run -c Release --project Plank.Benchmarks/Plank.Benchmarks.csproj -- --size-matrix 1000000 \
  | tee benchmarks/report/bench_comparison_size_matrix_run_20260224.txt
```

## Encoding Speed Comparison
Source: `benchmarks/report/bench_comparison_quick_perf_20260224.txt`

| Scenario (1,000,000 rows) | Plank (ms) | ParquetSharp (ms) | Parquet.Net (ms) | Fastest |
| --- | ---: | ---: | ---: | --- |
| `bool/plain` | 0.454 | 2.053 | 0.500 | Plank |
| `int64/dictionary` | 12.060 | 47.574 | 53.385 | Plank |

Raw snippet:
```text
| Scenario | Plank | Parquet.Net | ParquetSharp |
|---|---:|---:|---:|
| bool/plain | 0.454 ms | 0.500 ms | 2.053 ms |
| int64/dictionary | 12.060 ms | 53.385 ms | 47.574 ms |
```

## Encoding Size Comparison
Source: `benchmarks/report/parquet_file_size_matrix.md`

Representative comparisons (bytes; 1,000,000 rows):

| Type/Encoding | Plank | ParquetSharp | Parquet.Net | Smallest |
| --- | ---: | ---: | ---: | --- |
| `bool/plain` | 125127 | 125228 | 125201 | Plank |
| `int32/plain` | 4000133 | 4000417 | 4000264 | Plank |
| `int64/dictionary` | 10500170 | 10449947 | 12000326 | ParquetSharp |
| `string/dictionary` | 1398630 | 1400843 | 3023776 | Plank |

Raw snippet:
```text
| int64 | dictionary | 10500170 | 10449947 🚀 | 12000326 |
| string | dictionary | 1398630 🚀 | 1400843 | 3023776 |
```

## Key Findings
- Encoding speed (`quick-perf` scenarios): Plank is fastest in both measured cases.
- Encoding size: Plank is usually smallest in this run; notable exception is `int64/dictionary`, where ParquetSharp is smaller.

## TODO / Gaps
- TODO: If you want statistically stronger confidence, run multiple independent benchmark sessions and report variance across sessions (current report uses one session per command).
