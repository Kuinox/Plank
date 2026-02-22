# Dictionary Read Threshold Report (ParquetSharp)

## Benchmark Setup

- Date: 2026-02-22
- Benchmark: `Plank.Benchmarks/ParquetSharpDictionaryReadMatrixBdnBenchmark.cs`
- Reader: ParquetSharp (`ReadPlainWithParquetSharp` vs `ReadDictionaryWithParquetSharp`)
- Rows: 1,000,000
- Value type: `long`
- Uniqueness sweep: `1,2,5,10,20,30,40,50,60,70,80,90,100` percent
- Source CSV: `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.ParquetSharpDictionaryReadMatrixBdnBenchmark-report.csv`

## Results

| Unique % | Plain mean | Dictionary mean | Faster | Dict/Plain |
| --- | --- | --- | --- | --- |
| 1 | 1.775 ms | 1.540 ms | dictionary | 0.87x |
| 2 | 1.793 ms | 1.659 ms | dictionary | 0.93x |
| 5 | 1.714 ms | 1.067 ms | dictionary | 0.62x |
| 10 | 1.803 ms | 1.598 ms | dictionary | 0.89x |
| 20 | 1.591 ms | 1.939 ms | plain | 1.22x |
| 30 | 1.578 ms | 2.062 ms | plain | 1.31x |
| 40 | 1.751 ms | 2.021 ms | plain | 1.15x |
| 50 | 1.636 ms | 2.103 ms | plain | 1.29x |
| 60 | 1.762 ms | 2.455 ms | plain | 1.39x |
| 70 | 1.640 ms | 2.255 ms | plain | 1.38x |
| 80 | 1.658 ms | 2.279 ms | plain | 1.37x |
| 90 | 1.734 ms | 2.472 ms | plain | 1.43x |
| 100 | 1.617 ms | 2.548 ms | plain | 1.58x |

## Crossover

- Dictionary is consistently faster for this workload up to **10% unique**.
- Plain becomes consistently faster starting at **20% unique**.
- Crossover zone is between **10% and 20%** unique.

## Practical Recommendation

If optimizing for read speed (ParquetSharp-style readers):
- Prefer dictionary when estimated unique ratio is <= **10%**.
- Prefer plain when estimated unique ratio is >= **20%**.
- Use **15%** as a conservative default drop threshold, then tune with production data.

## Notes

- This result is for `long` with a uniform generator and uncompressed pages.
- Threshold can shift with data distribution, compression, and reader implementation.
- Keep this matrix benchmark as a regression gate when tuning `ShouldDropDictionary(...)`.
