using System.Text.Json.Serialization;

namespace Plank.Benchmarks.Published;

public sealed class PublishedBenchmarkReport
{
    public int SchemaVersion { get; init; } = 1;

    public required DateTimeOffset GeneratedAt { get; init; }

    public required EnvironmentDetails Environment { get; init; }

    public required ConfigurationDetails Configuration { get; init; }

    public required IReadOnlyList<SuiteResult> Suites { get; init; }

    public sealed class EnvironmentDetails
    {
        public required string Cpu { get; init; }

        public required int LogicalProcessors { get; init; }

        public required string OperatingSystem { get; init; }

        public required string DotNetVersion { get; init; }

        public required string Commit { get; init; }

        public required IReadOnlyDictionary<string, string> Libraries { get; init; }
    }

    public sealed class ConfigurationDetails
    {
        public required int Warmups { get; init; }

        public required int Iterations { get; init; }

        public required string Compression { get; init; }

        public required string DataPageVersion { get; init; }

        public required bool PageIndexes { get; init; }

        public required bool BloomFilters { get; init; }

        public required string RowGroupBoundaries { get; init; }

        public required string TimingBoundary { get; init; }

        public required bool Quick { get; init; }
    }

    public sealed class SuiteResult
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        public required IReadOnlyList<CaseResult> Cases { get; init; }
    }

    public sealed class CaseResult
    {
        public required string Id { get; init; }

        public required string Label { get; init; }

        public required string Encoding { get; init; }

        public required IReadOnlyList<string> DataTypes { get; init; }

        public required long RowCount { get; init; }

        public required long ValueCount { get; init; }

        public required int ColumnCount { get; init; }

        public required string ThroughputUnit { get; init; }

        public required IReadOnlyList<Measurement> Measurements { get; init; }

        public string? WinnerId { get; init; }

        public double? PlankSpeedup { get; init; }
    }

    public sealed class Measurement
    {
        public required string ImplementationId { get; init; }

        public required string Label { get; init; }

        public required int Threads { get; init; }

        public required bool Available { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UnavailableReason { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? MedianMilliseconds { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? P25Milliseconds { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? P75Milliseconds { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? VariationPercent { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Throughput { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? OutputBytes { get; init; }

        public IReadOnlyList<double> SamplesMilliseconds { get; init; } = [];
    }
}
