using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class PublishedBenchmarkQuickSmokeTests
{
    [Test]
    public async Task QuickRunCoversBothSuitesAndEveryWriter()
    {
        var realWorld = new[]
        {
            new PublishedBenchmarkDataSet
            {
                SuiteId = "real-world",
                Id = "sample",
                Label = "Sample",
                Encoding = "plain",
                ThroughputUnit = "million rows/s",
                Columns =
                [
                    new PublishedBenchmarkDataSet.Column
                    {
                        Name = "value",
                        Kind = BenchmarkColumnKind.Int64,
                        Nullable = false,
                        Values = [Enumerable.Range(0, 64).Select(static value => (long)value).ToArray()]
                    }
                ]
            }
        };
        var options = new PublishedBenchmarkOptions
        {
            Quick = true,
            Warmups = 0,
            Iterations = 1,
            WorkerCount = Math.Min(2, Environment.ProcessorCount)
        };

        var report = await PublishedBenchmarkRunner.RunAsync(realWorld, SyntheticBenchmarkData.Create(64, 2), options);

        await Assert.That(report.Suites.Select(static suite => suite.Id)).IsEquivalentTo(["real-world", "synthetic"]);
        foreach (var benchmarkCase in report.Suites.SelectMany(static suite => suite.Cases))
        {
            await Assert.That(benchmarkCase.Measurements.Count).IsEqualTo(5);
            await Assert.That(benchmarkCase.Measurements.Count(static result => result.Available)).IsGreaterThanOrEqualTo(4);
        }
        var unsupported = report.Suites.Single(static suite => suite.Id == "synthetic").Cases
            .SelectMany(static benchmarkCase => benchmarkCase.Measurements)
            .Where(static result => result.ImplementationId == "parquetnet-single" && !result.Available)
            .ToArray();
        await Assert.That(unsupported.Length).IsEqualTo(9);
    }
}
