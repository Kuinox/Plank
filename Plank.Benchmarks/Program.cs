using BenchmarkDotNet.Running;
using Plank.Benchmarks;
using Plank.Benchmarks.Published;

if (args is ["--published-write", ..])
{
    await PublishedBenchmarkCommand.RunAsync(args[1..]);
    return;
}

if (args is ["--published-read", ..])
{
    await PublishedReadBenchmarkCommand.RunAsync(args[1..]);
    return;
}

if (args is ["--audit-encodings", ..])
{
    await EncodingActualEncodingAudit.RunAsync();
    return;
}

if (args is ["--encoding-profile", ..])
{
    EncodingProfile.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(EncodingBenchmark).Assembly)
    .Run(args);
