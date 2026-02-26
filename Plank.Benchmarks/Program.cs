using BenchmarkDotNet.Running;
using Plank.Benchmarks;

BenchmarkSwitcher.FromTypes([typeof(EncodingBenchmark), typeof(StringDictionaryStrategyBenchmark),
    typeof(DictionaryImplementationBenchmark)])
    .Run(args);
