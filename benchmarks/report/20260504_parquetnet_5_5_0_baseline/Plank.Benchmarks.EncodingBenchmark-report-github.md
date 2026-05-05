```

BenchmarkDotNet v0.15.6, Windows 11 (10.0.22631.6199/23H2/2023Update/SunValley3)
AMD Ryzen 5 7640U w/ Radeon 760M Graphics 3.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.200-preview.0.26103.119
  [Host]     : .NET 10.0.2 (10.0.2, 10.0.225.61305), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.2 (10.0.2, 10.0.225.61305), X64 RyuJIT x86-64-v4


```
| Method               | Rows    | DataType | EncodingName         | Mean        | Error       | StdDev      | Median      | Ratio | RatioSD | Gen0      | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|--------------------- |-------- |--------- |--------------------- |------------:|------------:|------------:|------------:|------:|--------:|----------:|---------:|---------:|-----------:|------------:|
| **WritePlank**           | **1000000** | **bool**     | **byte_stream_split**    |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | bool     | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | bool     | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **bool**     | **delta_binary_packed**  |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | bool     | delta_binary_packed  |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | bool     | delta_binary_packed  |    425.6 μs |     4.57 μs |     3.57 μs |    425.0 μs |     ? |       ? |    0.4883 |        - |        - |     5224 B |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **bool**     | **delta_byte_array**     |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | bool     | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | bool     | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **bool**     | **delta(...)array [23]** |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | bool     | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | bool     | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **bool**     | **dictionary**           |  **3,110.4 μs** |    **13.12 μs** |    **10.25 μs** |  **3,109.3 μs** |  **1.00** |    **0.00** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | bool     | dictionary           |  5,191.1 μs |   101.78 μs |   125.00 μs |  5,226.2 μs |  1.67 |    0.04 |         - |        - |        - |   167752 B |          NA |
| WriteParquetNetAsync | 1000000 | bool     | dictionary           |    446.1 μs |     8.53 μs |     9.83 μs |    445.2 μs |  0.14 |    0.00 |    0.4883 |        - |        - |     5224 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **bool**     | **plain**                |  **1,149.9 μs** |    **17.88 μs** |    **15.85 μs** |  **1,142.8 μs** |  **1.00** |    **0.02** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | bool     | plain                |  5,184.3 μs |   103.00 μs |   232.48 μs |  5,184.5 μs |  4.51 |    0.21 |         - |        - |        - |   167752 B |          NA |
| WriteParquetNetAsync | 1000000 | bool     | plain                |    533.8 μs |     5.87 μs |     4.91 μs |    532.7 μs |  0.46 |    0.01 |         - |        - |        - |     5224 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **double**   | **byte_stream_split**    |  **4,608.2 μs** |    **89.40 μs** |   **113.07 μs** |  **4,569.2 μs** |  **1.00** |    **0.03** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | double   | byte_stream_split    |  9,747.3 μs |   181.18 μs |   141.45 μs |  9,735.0 μs |  2.12 |    0.06 |         - |        - |        - |  8072272 B |          NA |
| WriteParquetNetAsync | 1000000 | double   | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **double**   | **delta_binary_packed**  |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | double   | delta_binary_packed  |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | double   | delta_binary_packed  |  2,286.0 μs |    43.51 μs |    42.73 μs |  2,268.6 μs |     ? |       ? |         - |        - |        - |     7952 B |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **double**   | **delta_byte_array**     |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | double   | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | double   | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **double**   | **delta(...)array [23]** |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | double   | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | double   | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **double**   | **dictionary**           |  **9,621.8 μs** |    **88.59 μs** |    **73.98 μs** |  **9,600.9 μs** |  **1.00** |    **0.01** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | double   | dictionary           | 12,099.8 μs |   133.19 μs |   111.22 μs | 12,096.0 μs |  1.26 |    0.01 |   15.6250 |        - |        - |  1903656 B |          NA |
| WriteParquetNetAsync | 1000000 | double   | dictionary           | 42,161.5 μs | 2,645.45 μs | 7,800.17 μs | 42,909.0 μs |  4.38 |    0.81 |  125.0000 | 125.0000 | 125.0000 | 41944232 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **double**   | **plain**                |  **2,503.2 μs** |    **38.01 μs** |    **35.56 μs** |  **2,495.2 μs** |  **1.00** |    **0.02** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | double   | plain                |  7,152.8 μs |    79.51 μs |    66.39 μs |  7,159.0 μs |  2.86 |    0.05 |   31.2500 |  31.2500 |  31.2500 |  8072419 B |          NA |
| WriteParquetNetAsync | 1000000 | double   | plain                |  2,222.1 μs |    39.12 μs |    53.55 μs |  2,209.7 μs |  0.89 |    0.02 |         - |        - |        - |     7872 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **float**    | **byte_stream_split**    |  **2,827.2 μs** |    **30.71 μs** |    **27.22 μs** |  **2,820.4 μs** |  **1.00** |    **0.01** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | float    | byte_stream_split    |  5,436.2 μs |   104.91 μs |    87.60 μs |  5,450.6 μs |  1.92 |    0.03 |         - |        - |        - |  4055384 B |          NA |
| WriteParquetNetAsync | 1000000 | float    | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **float**    | **delta_binary_packed**  |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | float    | delta_binary_packed  |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | float    | delta_binary_packed  |  1,013.5 μs |    44.95 μs |   128.96 μs |  1,022.6 μs |     ? |       ? |         - |        - |        - |     6632 B |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **float**    | **delta_byte_array**     |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | float    | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | float    | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **float**    | **delta(...)array [23]** |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | float    | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | float    | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **float**    | **dictionary**           | **10,097.8 μs** |   **200.91 μs** |   **306.81 μs** | **10,055.7 μs** |  **1.00** |    **0.04** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | float    | dictionary           | 12,343.3 μs |   242.76 μs |   307.01 μs | 12,299.3 μs |  1.22 |    0.05 |         - |        - |        - |  1847240 B |          NA |
| WriteParquetNetAsync | 1000000 | float    | dictionary           | 37,652.1 μs | 1,033.95 μs | 3,016.09 μs | 37,751.4 μs |  3.73 |    0.32 |  125.0000 | 125.0000 | 125.0000 | 41904232 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **float**    | **plain**                |  **2,189.0 μs** |    **41.86 μs** |    **49.83 μs** |  **2,177.4 μs** |  **1.00** |    **0.03** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | float    | plain                |  5,197.5 μs |   103.33 μs |   148.19 μs |  5,169.4 μs |  2.38 |    0.08 |   15.6250 |  15.6250 |  15.6250 |  4055433 B |          NA |
| WriteParquetNetAsync | 1000000 | float    | plain                |  1,063.6 μs |    21.83 μs |    64.02 μs |  1,043.4 μs |  0.49 |    0.03 |         - |        - |        - |     6712 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int32**    | **byte_stream_split**    |  **2,467.0 μs** |    **34.35 μs** |    **30.45 μs** |  **2,471.7 μs** |  **1.00** |    **0.02** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int32    | byte_stream_split    |  4,649.9 μs |    92.85 μs |   172.10 μs |  4,635.4 μs |  1.89 |    0.07 |   15.6250 |  15.6250 |  15.6250 |  4055934 B |          NA |
| WriteParquetNetAsync | 1000000 | int32    | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int32**    | **delta_binary_packed**  |  **2,212.1 μs** |    **25.64 μs** |    **21.41 μs** |  **2,201.7 μs** |  **1.00** |    **0.01** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int32    | delta_binary_packed  |  3,604.5 μs |    34.63 μs |    27.04 μs |  3,607.3 μs |  1.63 |    0.02 |    7.8125 |        - |        - |    97056 B |          NA |
| WriteParquetNetAsync | 1000000 | int32    | delta_binary_packed  |  3,157.6 μs |    53.17 μs |    47.14 μs |  3,162.7 μs |  1.43 |    0.02 |   89.8438 |        - |        - |   762808 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int32**    | **delta_byte_array**     |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | int32    | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | int32    | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int32**    | **delta(...)array [23]** |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | int32    | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | int32    | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int32**    | **dictionary**           |  **6,794.6 μs** |    **78.91 μs** |    **65.89 μs** |  **6,778.8 μs** |  **1.00** |    **0.01** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int32    | dictionary           | 14,480.6 μs |   455.68 μs | 1,336.42 μs | 14,151.6 μs |  2.13 |    0.20 |         - |        - |        - |  2582800 B |          NA |
| WriteParquetNetAsync | 1000000 | int32    | dictionary           | 15,121.4 μs |   121.17 μs |   107.41 μs | 15,109.9 μs |  2.23 |    0.03 |  125.0000 | 125.0000 | 125.0000 | 42264898 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int32**    | **plain**                |  **1,615.2 μs** |    **25.78 μs** |    **22.85 μs** |  **1,614.8 μs** |  **1.00** |    **0.02** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int32    | plain                |  3,216.5 μs |    62.14 μs |    78.59 μs |  3,225.4 μs |  1.99 |    0.05 |   27.3438 |  23.4375 |  23.4375 |  4055952 B |          NA |
| WriteParquetNetAsync | 1000000 | int32    | plain                |    718.8 μs |    14.34 μs |    11.20 μs |    714.8 μs |  0.45 |    0.01 |         - |        - |        - |     6872 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int64**    | **byte_stream_split**    |  **3,322.1 μs** |    **51.35 μs** |    **45.52 μs** |  **3,320.0 μs** |  **1.00** |    **0.02** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int64    | byte_stream_split    |  8,619.2 μs |   233.84 μs |   647.96 μs |  8,360.7 μs |  2.59 |    0.20 |   31.2500 |  31.2500 |  31.2500 |  8072879 B |          NA |
| WriteParquetNetAsync | 1000000 | int64    | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int64**    | **delta_binary_packed**  |  **2,036.6 μs** |    **20.83 μs** |    **18.46 μs** |  **2,032.9 μs** |  **1.00** |    **0.01** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int64    | delta_binary_packed  |  3,660.9 μs |    33.40 μs |    26.07 μs |  3,662.1 μs |  1.80 |    0.02 |    7.8125 |        - |        - |    91400 B |          NA |
| WriteParquetNetAsync | 1000000 | int64    | delta_binary_packed  |  3,163.5 μs |    37.66 μs |    33.38 μs |  3,160.9 μs |  1.55 |    0.02 |   89.8438 |        - |        - |   755896 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int64**    | **delta_byte_array**     |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | int64    | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | int64    | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int64**    | **delta(...)array [23]** |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | int64    | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | int64    | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int64**    | **dictionary**           |  **7,260.1 μs** |    **72.70 μs** |    **68.01 μs** |  **7,261.0 μs** |  **1.00** |    **0.01** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int64    | dictionary           | 73,478.9 μs | 1,385.34 μs | 2,115.57 μs | 72,807.1 μs | 10.12 |    0.30 |         - |        - |        - | 10521680 B |          NA |
| WriteParquetNetAsync | 1000000 | int64    | dictionary           | 23,499.9 μs |   288.89 μs |   270.22 μs | 23,406.3 μs |  3.24 |    0.05 |  133.3333 | 133.3333 | 133.3333 | 49867195 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **int64**    | **plain**                |  **1,788.7 μs** |    **14.44 μs** |    **17.74 μs** |  **1,791.7 μs** |  **1.00** |    **0.01** |         **-** |        **-** |        **-** |          **-** |          **NA** |
| WriteParquetSharp    | 1000000 | int64    | plain                |  5,317.8 μs |   104.37 μs |   201.08 μs |  5,258.3 μs |  2.97 |    0.11 |   31.2500 |  31.2500 |  31.2500 |  8072825 B |          NA |
| WriteParquetNetAsync | 1000000 | int64    | plain                |  1,934.4 μs |    24.94 μs |    30.63 μs |  1,939.4 μs |  1.08 |    0.02 |         - |        - |        - |     8032 B |          NA |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **string**   | **byte_stream_split**    |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | string   | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | string   | byte_stream_split    |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **string**   | **delta_binary_packed**  |          **NA** |          **NA** |          **NA** |          **NA** |     **?** |       **?** |        **NA** |       **NA** |       **NA** |         **NA** |           **?** |
| WriteParquetSharp    | 1000000 | string   | delta_binary_packed  |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
| WriteParquetNetAsync | 1000000 | string   | delta_binary_packed  | 24,874.1 μs |   485.70 μs |   770.38 μs | 24,508.8 μs |     ? |       ? | 3812.5000 |        - |        - | 44041744 B |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **string**   | **delta_byte_array**     | **23,113.2 μs** |   **402.52 μs** |   **376.52 μs** | **23,043.0 μs** |  **1.00** |    **0.02** |         **-** |        **-** |        **-** |      **128 B** |        **1.00** |
| WriteParquetSharp    | 1000000 | string   | delta_byte_array     | 45,152.9 μs |   812.19 μs |   759.73 μs | 44,849.0 μs |  1.95 |    0.04 |  909.0909 | 181.8182 |        - | 10080400 B |   78,753.12 |
| WriteParquetNetAsync | 1000000 | string   | delta_byte_array     |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **string**   | **delta(...)array [23]** | **19,407.4 μs** |   **542.15 μs** | **1,529.14 μs** | **19,125.5 μs** |  **1.01** |    **0.11** |         **-** |        **-** |        **-** |      **128 B** |        **1.00** |
| WriteParquetSharp    | 1000000 | string   | delta(...)array [23] | 39,956.1 μs |   763.43 μs |   637.50 μs | 39,966.7 μs |  2.07 |    0.16 |  923.0769 | 153.8462 |        - | 15818976 B |  123,585.75 |
| WriteParquetNetAsync | 1000000 | string   | delta(...)array [23] |          NA |          NA |          NA |          NA |     ? |       ? |        NA |       NA |       NA |         NA |           ? |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **string**   | **dictionary**           | **24,719.1 μs** |   **460.92 μs** |   **661.04 μs** | **24,565.5 μs** |  **1.00** |    **0.04** |         **-** |        **-** |        **-** |      **129 B** |        **1.00** |
| WriteParquetSharp    | 1000000 | string   | dictionary           | 46,164.2 μs |   813.75 μs |   761.18 μs | 46,225.9 μs |  1.87 |    0.06 |  909.0909 |  90.9091 |        - |  9700600 B |   75,198.45 |
| WriteParquetNetAsync | 1000000 | string   | dictionary           | 30,850.7 μs |   559.11 μs |   436.51 μs | 30,847.4 μs |  1.25 |    0.04 |  187.5000 | 187.5000 | 187.5000 | 35559272 B |  275,653.27 |
|                      |         |          |                      |             |             |             |             |       |         |           |          |          |            |             |
| **WritePlank**           | **1000000** | **string**   | **plain**                | **19,322.3 μs** |   **385.67 μs** |   **714.86 μs** | **19,066.0 μs** |  **1.00** |    **0.05** |         **-** |        **-** |        **-** |      **128 B** |        **1.00** |
| WriteParquetSharp    | 1000000 | string   | plain                | 39,520.9 μs |   644.83 μs |   603.17 μs | 39,352.3 μs |  2.05 |    0.08 |  923.0769 | 153.8462 |        - | 19758896 B |  154,366.38 |
| WriteParquetNetAsync | 1000000 | string   | plain                | 26,782.6 μs |   535.59 μs |   733.12 μs | 26,617.0 μs |  1.39 |    0.06 | 3812.5000 |        - |        - | 44041744 B |  344,076.12 |

Benchmarks with issues:
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=bool, EncodingName=byte_stream_split]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=bool, EncodingName=byte_stream_split]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=bool, EncodingName=byte_stream_split]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta_binary_packed]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta_binary_packed]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta_byte_array]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=bool, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=double, EncodingName=byte_stream_split]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta_binary_packed]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta_binary_packed]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta_byte_array]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=double, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=float, EncodingName=byte_stream_split]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta_binary_packed]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta_binary_packed]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta_byte_array]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=float, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=int32, EncodingName=byte_stream_split]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=int32, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=int32, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=int32, EncodingName=delta_byte_array]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=int32, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=int32, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=int32, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=int64, EncodingName=byte_stream_split]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=int64, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=int64, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=int64, EncodingName=delta_byte_array]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=int64, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=int64, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=int64, EncodingName=delta(...)array [23]]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=string, EncodingName=byte_stream_split]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=string, EncodingName=byte_stream_split]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=string, EncodingName=byte_stream_split]
  EncodingBenchmark.WritePlank: DefaultJob [Rows=1000000, DataType=string, EncodingName=delta_binary_packed]
  EncodingBenchmark.WriteParquetSharp: DefaultJob [Rows=1000000, DataType=string, EncodingName=delta_binary_packed]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=string, EncodingName=delta_byte_array]
  EncodingBenchmark.WriteParquetNetAsync: DefaultJob [Rows=1000000, DataType=string, EncodingName=delta(...)array [23]]
