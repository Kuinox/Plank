using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class PublishedReadBenchmarkQuickSmokeTests
{
    [Test]
    public async Task QuickRunCoversBothSuitesAndEveryReader()
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

        var report = await PublishedReadBenchmarkRunner.RunAsync(
            realWorld, SyntheticBenchmarkData.Create(64, 2), options);

        await Assert.That(report.Suites.Select(static suite => suite.Id))
            .IsEquivalentTo(["real-world", "synthetic"]);
        foreach (var benchmarkCase in report.Suites.SelectMany(static suite => suite.Cases))
        {
            await Assert.That(benchmarkCase.Measurements.Count).IsEqualTo(5);
            await Assert.That(benchmarkCase.Measurements.Count(static result => result.Available))
                .IsGreaterThanOrEqualTo(4);
        }
        var rle = report.Suites.Single(static suite => suite.Id == "synthetic").Cases
            .Single(static benchmarkCase => benchmarkCase.Encoding == "rle");
        await Assert.That(rle.Measurements.Single(static result =>
            result.ImplementationId == "parquetnet-single").Available).IsFalse();
    }

    [Test]
    public async Task ThreeColumnRunUsesDedicatedWorkersAndPreservesTheFingerprint()
    {
        var options = new PublishedBenchmarkOptions
        {
            Quick = true,
            Warmups = 1,
            Iterations = 1,
            WorkerCount = Math.Min(3, Environment.ProcessorCount),
            CaseId = "int32-delta-binary-packed"
        };
        var synthetic = SyntheticBenchmarkData.Create(64, 3, options.CaseId);

        var report = await PublishedReadBenchmarkRunner.RunAsync([], synthetic, options);

        var benchmarkCase = report.Suites.Single(static suite => suite.Id == "synthetic").Cases.Single();
        var plankMulti = benchmarkCase.Measurements.Single(static measurement =>
            measurement.ImplementationId == "plank-multi");
        await Assert.That(plankMulti.Available).IsTrue();
        await Assert.That(plankMulti.SamplesMilliseconds).Count().IsEqualTo(1);
    }
}
