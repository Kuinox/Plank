```

BenchmarkDotNet v0.15.6, Windows 11 (10.0.22631.6199/23H2/2023Update/SunValley3)
AMD Ryzen 5 7640U w/ Radeon 760M Graphics 3.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.300-preview.0.26177.108
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v4


```
| Method               | Rows    | Case        | Mean     | Error     | StdDev    | Median   | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------- |-------- |------------ |---------:|----------:|----------:|---------:|------:|--------:|----------:|------------:|
| WritePlank           | 1000000 | int32/plain | 1.337 ms | 0.0266 ms | 0.0633 ms | 1.310 ms |  1.00 |    0.07 |         - |          NA |
| WriteParquetNetAsync | 1000000 | int32/plain | 1.269 ms | 0.0252 ms | 0.0569 ms | 1.248 ms |  0.95 |    0.06 |    7016 B |          NA |
