using BenchmarkDotNet.Running;
using Plank.Benchmarks;

BenchmarkSwitcher.FromTypes([typeof(EncodingMatrixBdnBenchmark)]).Run(args);
