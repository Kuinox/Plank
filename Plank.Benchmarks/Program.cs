using BenchmarkDotNet.Running;
using Plank.Benchmarks;

if (args is ["--audit-encodings", ..])
{
    await EncodingActualEncodingAudit.RunAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(EncodingBenchmark).Assembly)
    .Run(args);
