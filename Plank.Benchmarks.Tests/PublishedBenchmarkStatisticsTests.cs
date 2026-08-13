using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class PublishedBenchmarkStatisticsTests
{
    [Test]
    public async Task SummaryUsesMedianAndInterquartileVariation()
    {
        var summary = PublishedBenchmarkStatistics.Summarize([9, 1, 7, 3, 5, 11, 13]);

        await Assert.That(summary.Median).IsEqualTo(7);
        await Assert.That(summary.P25).IsEqualTo(4);
        await Assert.That(summary.P75).IsEqualTo(10);
        await Assert.That(Math.Round(summary.VariationPercent, 6)).IsEqualTo(Math.Round(600d / 7, 6));
    }

    [Test]
    public async Task WinnerUsesFastestResultAndFastestPlankForSpeedup()
    {
        PublishedBenchmarkReport.Measurement[] measurements =
        [
            Measurement("plank-single", 20),
            Measurement("plank-multi", 50),
            Measurement("parquetsharp-single", 25),
            Measurement("parquetsharp-multi", 40),
            Measurement("parquetnet-single", 10)
        ];

        var winner = PublishedBenchmarkStatistics.FindWinner(measurements);

        await Assert.That(winner.ImplementationId).IsEqualTo("plank-multi");
        await Assert.That(winner.PlankSpeedup).IsEqualTo(1.25);
    }

    static PublishedBenchmarkReport.Measurement Measurement(string id, double throughput)
        => new()
        {
            ImplementationId = id,
            Label = id,
            Threads = 1,
            Available = true,
            Throughput = throughput
        };
}
