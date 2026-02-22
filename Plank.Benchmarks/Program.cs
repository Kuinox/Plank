using BenchmarkDotNet.Running;
using Plank.Benchmarks;

BenchmarkSwitcher.FromTypes([typeof(EncodingMatrixBdnBenchmark), typeof(ParquetSharpDictionaryReadMatrixBdnBenchmark)])
    .Run(args);
