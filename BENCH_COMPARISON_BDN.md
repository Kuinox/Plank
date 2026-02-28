# Encoding All Combinations Benchmark Results

Run date: `2026-02-28T18:22:54+01:00`

Source reports:
- Benchmark data: `BenchmarkDotNet.Artifacts/results/Plank.Benchmarks.EncodingBenchmark-report.csv`
- Compatibility matrix used for supported/unsupported filtering: `benchmarks/report/compat_matrix_run_20260227_170914.csv`

## Notes

- This matrix includes only combinations marked as `supported` by the compatibility matrix.
- Unsupported combinations are rendered as `NA (unsupported)`.
- Benchmark run executed 52 benchmark cases (supported-only selection).

## Throughput (Mean, lower is better)

| Type | Encoding | Plank | ParquetSharp | Parquet.Net | Winner |
| --- | --- | --- | --- | --- | --- |
| bool | plain | 240.4 μs | 2,014.7 μs | 401.9 μs | Plank |
| bool | dictionary | 1,768.3 μs | NA (unsupported) | NA (unsupported) | Plank |
| int32 | plain | 311.3 μs | 846.6 μs | 564.0 μs | Plank |
| int32 | dictionary | 4,747.5 μs | 7,521.0 μs | 12,948.4 μs | Plank |
| int32 | delta_binary_packed | 1,202.8 μs | 2,261.4 μs | 2,638.3 μs | Plank |
| int32 | byte_stream_split | 1,011.9 μs | 1,357.7 μs | NA (unsupported) | Plank |
| int64 | plain | 466.2 μs | 2,342.9 μs | 890.4 μs | Plank |
| int64 | dictionary | 4,760.8 μs | 44,973.7 μs | 17,697.7 μs | Plank |
| int64 | delta_binary_packed | 1,151.1 μs | 2,770.2 μs | 2,720.1 μs | Plank |
| int64 | byte_stream_split | 1,829.9 μs | 3,100.3 μs | NA (unsupported) | Plank |
| float | plain | 312.7 μs | 1,207.0 μs | 605.5 μs | Plank |
| float | dictionary | 6,171.4 μs | 7,084.1 μs | 21,373.8 μs | Plank |
| float | byte_stream_split | 1,061.3 μs | 1,761.2 μs | NA (unsupported) | Plank |
| double | plain | 453.4 μs | 2,232.7 μs | 987.9 μs | Plank |
| double | dictionary | 6,765.4 μs | 7,636.5 μs | 21,100.7 μs | Plank |
| double | byte_stream_split | 2,403.8 μs | 3,167.9 μs | NA (unsupported) | Plank |
| string | plain | 6,319.9 μs | 21,542.7 μs | 22,704.3 μs | Plank |
| string | dictionary | 12,429.4 μs | 27,733.8 μs | 22,898.5 μs | Plank |
| string | delta_byte_array | 12,000.9 μs | 27,907.0 μs | NA (unsupported) | Plank |

## Winner By Encoding

- `plain`: **Plank** (wins all supported types)
- `dictionary`: **Plank** (wins all supported types)
- `delta_binary_packed`: **Plank** (`int32`, `int64`)
- `byte_stream_split`: **Plank** (`int32`, `int64`, `float`, `double`)
- `delta_byte_array`: **Plank** (`string`)

## Allocation (bytes/op)

| Type | Encoding | Plank | ParquetSharp | Parquet.Net |
| --- | --- | --- | --- | --- |
| bool | plain | 0 B | 167752 B | 5248 B |
| bool | dictionary | 0 B | NA (unsupported) | NA (unsupported) |
| int32 | plain | 0 B | 4055902 B | 6816 B |
| int32 | dictionary | 0 B | 2582800 B | 42264941 B |
| int32 | delta_binary_packed | 0 B | 97056 B | 762832 B |
| int32 | byte_stream_split | 0 B | 4055917 B | NA (unsupported) |
| int64 | plain | 0 B | 8072810 B | 8056 B |
| int64 | dictionary | 0 B | 10521680 B | 49867884 B |
| int64 | delta_binary_packed | 0 B | 91400 B | 755920 B |
| int64 | byte_stream_split | 0 B | 8072899 B | NA (unsupported) |
| float | plain | 0 B | 4055488 B | 6656 B |
| float | dictionary | 0 B | 1847240 B | 41904316 B |
| float | byte_stream_split | 0 B | 4055554 B | NA (unsupported) |
| double | plain | 0 B | 8072453 B | 7896 B |
| double | dictionary | 0 B | 1903656 B | 41944316 B |
| double | byte_stream_split | 0 B | 8072505 B | NA (unsupported) |
| string | plain | 0 B | 19758896 B | 44041768 B |
| string | dictionary | 0 B | 9700600 B | 35558814 B |
| string | delta_byte_array | 0 B | 10080400 B | NA (unsupported) |
