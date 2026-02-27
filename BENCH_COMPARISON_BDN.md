# Encoding All Combinations Benchmark Results

Source reports:
- Plank numbers: `BenchmarkDotNet.Artifacts/na-check/results/Plank.Benchmarks.EncodingBenchmark-report-github.md`

## Notes

- Rows where the requested encoding is not actually produced are removed from the table.
- Parquet.Net unsupported cases are marked as `NA` in raw matrix generation and excluded when the Plank row is unsupported.

## Throughput (Mean us, lower is better)

| Type | Encoding | Plank | ParquetSharp | Parquet.Net | Fastest |
| --- | --- | --- | --- | --- | --- |
| bool | plain | 15,085.9 μs | 41,836.1 μs | 27,408.8 μs | Plank |
| bool | dictionary | 19,532.4 μs | 38,175.3 μs | 27,613.4 μs | Plank |
| bool | delta_binary_packed | NA | NA | 32,539.7 μs | Parquet.Net |
| int32 | plain | 14,896.8 μs | 38,173.8 μs | 30,130.5 μs | Plank |
| int32 | dictionary | 37,173.5 μs | 41,827.8 μs | 159,134.7 μs | Plank |
| int32 | delta_binary_packed | 15,214.1 μs | 33,918.4 μs | 41,118.3 μs | Plank |
| int32 | byte_stream_split | 17,103.8 μs | 44,963.5 μs | NA | Plank |
| int64 | plain | 17,662.3 μs | 46,235.6 μs | 40,460.1 μs | Plank |
| int64 | dictionary | 38,769.1 μs | 115,885.5 μs | 243,727.3 μs | Plank |
| int64 | delta_binary_packed | 15,455.5 μs | 35,577.6 μs | 42,533.4 μs | Plank |
| int64 | byte_stream_split | 18,728.4 μs | 45,106.7 μs | NA | Plank |
| float | plain | 17,196.5 μs | 40,119.6 μs | 29,372.6 μs | Plank |
| float | dictionary | 51,314.0 μs | 45,182.9 μs | 193,086.5 μs | ParquetSharp |
| float | delta_binary_packed | NA | NA | 35,117.9 μs | Parquet.Net |
| float | byte_stream_split | 24,766.2 μs | 47,800.3 μs | NA | Plank |
| double | plain | 21,631.0 μs | 57,532.7 μs | 37,162.1 μs | Plank |
| double | dictionary | 59,521.9 μs | 49,267.7 μs | 170,814.8 μs | ParquetSharp |
| double | delta_binary_packed | NA | NA | 35,553.4 μs | Parquet.Net |
| double | byte_stream_split | 21,309.6 μs | 49,700.3 μs | NA | Plank |
| string | plain | 31,022.3 μs | 97,559.3 μs | 76,579.8 μs | Plank |
| string | dictionary | 102,102.7 μs | 109,292.3 μs | 88,206.1 μs | Parquet.Net |
| string | delta_binary_packed | NA | NA | 73,989.4 μs | Parquet.Net |
| string | delta_byte_array | 46,048.3 μs | 94,754.0 μs | NA | Plank |

## Allocation (bytes/op)

| Type | Encoding | Plank | ParquetSharp | Parquet.Net |
| --- | --- | --- | --- | --- |
| bool | plain | 0 B | 168728 B | 5368 B |
| bool | dictionary | 0 B | 168728 B | 5368 B |
| bool | delta_binary_packed | NA | NA | 5368 B |
| int32 | plain | 0 B | 4056816 B | 6936 B |
| int32 | dictionary | 0 B | 2583776 B | 42264888 B |
| int32 | delta_binary_packed | 0 B | 98032 B | 762952 B |
| int32 | byte_stream_split | 0 B | 4056816 B | NA |
| int64 | plain | 0 B | 8073648 B | 8176 B |
| int64 | dictionary | 0 B | 10522656 B | 49867208 B |
| int64 | delta_binary_packed | 0 B | 92376 B | 756040 B |
| int64 | byte_stream_split | 0 B | 8073648 B | NA |
| float | plain | 0 B | 4056360 B | 6776 B |
| float | dictionary | 0 B | 1848216 B | 41904256 B |
| float | delta_binary_packed | NA | NA | 6776 B |
| float | byte_stream_split | 0 B | 4056360 B | NA |
| double | plain | 0 B | 8073248 B | 8016 B |
| double | dictionary | 0 B | 1904632 B | 41944256 B |
| double | delta_binary_packed | NA | NA | 8016 B |
| double | byte_stream_split | 0 B | 8073248 B | NA |
| string | plain | 0 B | 19759840 B | 44041888 B |
| string | dictionary | 0 B | 9701544 B | 35558976 B |
| string | delta_binary_packed | NA | NA | 44041888 B |
| string | delta_byte_array | 0 B | 10081344 B | NA |

## Dictionary Encoding: Plank Before vs After

| Type   | Old Plank   | New Plank   | Speedup | vs ParquetSharp (new) |
|--------|------------:|------------:|--------:|-----------------------:|
| bool   |   8,819.9 us|   4,231.3 us| 2.08x   | 2.06x slower           |
| int32  |  10,080.5 us|   4,546.0 us| 2.22x   | 1.72x faster           |
| int64  |   5,419.8 us|   4,687.4 us| 1.16x   | 9.89x faster           |
| float  |  21,457.2 us|   6,719.0 us| 3.19x   | 1.11x faster           |
| double |  14,373.6 us|   7,064.0 us| 2.03x   | 1.14x faster           |
| string |  19,011.3 us|  12,883.1 us| 1.48x   | 2.35x faster           |

