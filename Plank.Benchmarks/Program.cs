using BenchmarkDotNet.Running;
using Plank.Benchmarks;
using Plank.Benchmarks.EncodingRegression;
using Plank.Benchmarks.Published;

if (args is ["--encoding-regression", ..])
{
    await EncodingRegressionCommand.RunAsync(args[1..]);
    return 0;
}

if (args is ["--encoding-regression-compare", ..])
    return await EncodingRegressionCommand.CompareAsync(args[1..]);

if (args is ["--published-write", ..])
{
    await PublishedBenchmarkCommand.RunAsync(args[1..]);
    return 0;
}

if (args is ["--published-read", ..])
{
    await PublishedReadBenchmarkCommand.RunAsync(args[1..]);
    return 0;
}

if (args is ["--audit-encodings", ..])
{
    await EncodingActualEncodingAudit.RunAsync();
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(EncodingBenchmark).Assembly)
    .Run(args);
return 0;
