using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class PublishedBenchmarkCaseFilterTests
{
    [Test]
    public async Task PublishedRunsWarmPastTieredCompilationByDefault()
    {
        await Assert.That(PublishedBenchmarkCommand.CreateOptions([]).Warmups).IsEqualTo(8);
        await Assert.That(PublishedBenchmarkCommand.CreateOptions(["--quick"]).Warmups).IsEqualTo(1);
        await Assert.That(PublishedBenchmarkCommand.CreateOptions(["--warmups", "3"]).Warmups).IsEqualTo(3);
    }

    [Test]
    public async Task CaseOptionRunsOnlyTheMatchingCaseInEachSuite()
    {
        var parsed = PublishedBenchmarkCommand.CreateOptions(["--quick", "--case", "target"]);
        await Assert.That(parsed.CaseId).IsEqualTo("target");
        await Assert.That(PublishedBenchmarkCommand.GetDefaultOutputPath("root", read: false, parsed))
            .IsEqualTo(Path.Combine("root", "artifacts", "benchmarks", "write-case-v1.json"));
        await Assert.That(PublishedBenchmarkCommand.GetDefaultOutputPath("root", read: true, parsed))
            .IsEqualTo(Path.Combine("root", "artifacts", "benchmarks", "read-case-v1.json"));

        var realWorld = new[] { CreateDataSet("real-world", "target"), CreateDataSet("real-world", "skip") };
        var synthetic = new[] { CreateDataSet("synthetic", "skip"), CreateDataSet("synthetic", "target") };
        var options = new PublishedBenchmarkOptions
        {
            Quick = true,
            Warmups = 0,
            Iterations = 1,
            WorkerCount = 1,
            CaseId = "target"
        };

        var writeReport = await PublishedBenchmarkRunner.RunAsync(realWorld, synthetic, options);
        var readReport = await PublishedReadBenchmarkRunner.RunAsync(realWorld, synthetic, options);

        foreach (var report in new[] { writeReport, readReport })
        foreach (var suite in report.Suites)
        {
            await Assert.That(suite.Cases.Count).IsEqualTo(1);
            await Assert.That(suite.Cases[0].Id).IsEqualTo("target");
        }
    }

    [Test]
    public async Task UnknownCaseIsRejected()
    {
        var data = new[] { CreateDataSet("synthetic", "known") };
        var options = new PublishedBenchmarkOptions
        {
            Warmups = 0,
            Iterations = 1,
            WorkerCount = 1,
            CaseId = "typo"
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PublishedBenchmarkRunner.RunAsync([], data, options));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PublishedReadBenchmarkRunner.RunAsync([], data, options));
    }

    [Test]
    public async Task DatasetFactoriesApplyCaseBeforeAllocatingOtherCases()
    {
        var synthetic = SyntheticBenchmarkData.Create(1, 1);
        foreach (var dataSet in synthetic)
        {
            var selected = SyntheticBenchmarkData.Create(1, 1, dataSet.Id);
            await Assert.That(selected.Select(item => item.Id))
                .IsEquivalentTo(new[] { dataSet.Id });
        }

        string[] taxiCaseIds =
        [
            "taxi-plain", "taxi-dictionary",
            "int32-plain", "int32-dictionary", "int32-delta-binary-packed", "int32-byte-stream-split",
            "int64-plain", "int64-dictionary", "int64-delta-binary-packed", "int64-byte-stream-split",
            "timestamps-plain", "timestamps-dictionary", "timestamps-delta-binary-packed",
            "timestamps-byte-stream-split",
            "doubles-plain", "doubles-dictionary", "doubles-byte-stream-split",
            "strings-plain", "strings-dictionary", "strings-delta-length-byte-array",
            "strings-delta-byte-array"
        ];
        foreach (var caseId in taxiCaseIds)
            await Assert.That(TaxiBenchmarkData.IsCaseId(caseId)).IsTrue();

        await Assert.That(TaxiBenchmarkData.IsCaseId("string-delta-byte-array")).IsFalse();
    }

    static PublishedBenchmarkDataSet CreateDataSet(string suiteId, string id)
        => new()
        {
            SuiteId = suiteId,
            Id = id,
            Label = id,
            Encoding = "plain",
            ThroughputUnit = "million rows/s",
            Columns =
            [
                new PublishedBenchmarkDataSet.Column
                {
                    Name = "value",
                    Kind = BenchmarkColumnKind.Int64,
                    Nullable = false,
                    Values = [Enumerable.Range(0, 32).Select(static value => (long)value).ToArray()]
                }
            ]
        };
}
