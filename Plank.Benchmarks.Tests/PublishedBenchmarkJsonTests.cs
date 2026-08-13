using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class PublishedBenchmarkJsonTests
{
    [Test]
    public async Task RoundTripPreservesUnavailableMeasurements()
    {
        var report = new PublishedBenchmarkReport
        {
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Environment = new PublishedBenchmarkReport.EnvironmentDetails
            {
                Cpu = "test",
                LogicalProcessors = 4,
                OperatingSystem = "test",
                DotNetVersion = "test",
                Commit = "abc",
                Libraries = new Dictionary<string, string>()
            },
            Configuration = new PublishedBenchmarkReport.ConfigurationDetails
            {
                Warmups = 2,
                Iterations = 7,
                Compression = "none",
                DataPageVersion = "V1",
                PageIndexes = false,
                BloomFilters = false,
                RowGroupBoundaries = "same",
                TimingBoundary = "complete write",
                Quick = false
            },
            Suites =
            [
                new PublishedBenchmarkReport.SuiteResult
                {
                    Id = "synthetic",
                    Label = "Synthetic",
                    Cases =
                    [
                        new PublishedBenchmarkReport.CaseResult
                        {
                            Id = "delta-byte-array",
                            Label = "Delta byte array",
                            Encoding = "delta_byte_array",
                            DataTypes = ["string"],
                            RowCount = 10,
                            ValueCount = 20,
                            ColumnCount = 2,
                            ThroughputUnit = "million values/s",
                            Measurements =
                            [
                                new PublishedBenchmarkReport.Measurement
                                {
                                    ImplementationId = "parquetnet-single",
                                    Label = "Parquet.Net (1 thread)",
                                    Threads = 1,
                                    Available = false,
                                    UnavailableReason = "unsupported"
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var json = PublishedBenchmarkJson.Serialize(report);
        var restored = PublishedBenchmarkJson.Deserialize(json);
        var measurement = restored.Suites[0].Cases[0].Measurements[0];

        await Assert.That(restored.SchemaVersion).IsEqualTo(1);
        await Assert.That(restored.Suites[0].Cases[0].DataTypes).IsEquivalentTo(["string"]);
        await Assert.That(measurement.Available).IsFalse();
        await Assert.That(measurement.UnavailableReason).IsEqualTo("unsupported");
    }
}
