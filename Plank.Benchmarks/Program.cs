using BenchmarkDotNet.Running;
using Plank.Benchmarks;

if (args.Any(static arg => string.Equals(arg, "--size-matrix", StringComparison.OrdinalIgnoreCase)))
{
    var exitCode = await EncodingSizeComparisonRunner.RunAsync(args).ConfigureAwait(false);
    Environment.ExitCode = exitCode;
    return;
}

BenchmarkSwitcher.FromTypes([typeof(EncodingMatrixBdnBenchmark), typeof(ParquetSharpDictionaryReadMatrixBdnBenchmark)])
    .Run(args);
