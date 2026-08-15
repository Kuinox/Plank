using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plank.Benchmarks.EncodingRegression;

public sealed class EncodingRegressionReport
{
    public required DateTimeOffset GeneratedAt { get; init; }

    public required string Label { get; init; }

    public required string Commit { get; init; }

    public required EnvironmentDetails Environment { get; init; }

    public required ConfigurationDetails Configuration { get; init; }

    public required IReadOnlyList<CaseResult> Cases { get; init; }

    public sealed class EnvironmentDetails
    {
        public required string RuntimeVersion { get; init; }

        public required string OperatingSystem { get; init; }

        public required string Architecture { get; init; }

        public required int ProcessorCount { get; init; }
    }

    public sealed class ConfigurationDetails
    {
        public required int Rows { get; init; }

        public required int Warmups { get; init; }

        public required int Iterations { get; init; }

        public required string TimingBoundary { get; init; }
    }

    public sealed class CaseResult
    {
        public required string Id { get; init; }

        public required string DataType { get; init; }

        public required string Encoding { get; init; }

        public required string Repetition { get; init; }

        /// <summary>"ok" when the case encoded successfully, otherwise "failed".</summary>
        public required string Status { get; init; }

        public string? Error { get; init; }

        public required int RowCount { get; init; }

        public required long ValueCount { get; init; }

        /// <summary>SHA-256 of the complete written file. Pure refactors must not change this.</summary>
        public string? OutputSha256 { get; init; }

        public long? OutputBytes { get; init; }

        /// <summary>
        /// Fastest observed iteration. Contention and GC only ever add time, so under a shared runner
        /// this converges on the true cost far more stably than the median. Regressions compare on this.
        /// </summary>
        public double? MinMicroseconds { get; init; }

        public double? MedianMicroseconds { get; init; }

        public double? P25Microseconds { get; init; }

        public double? P75Microseconds { get; init; }

        public double? VariationPercent { get; init; }

        public double? ValuesPerSecond { get; init; }
    }
}

static class EncodingRegressionJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string Serialize(EncodingRegressionReport report)
        => JsonSerializer.Serialize(report, Options);

    internal static EncodingRegressionReport Deserialize(string json)
        => JsonSerializer.Deserialize<EncodingRegressionReport>(json, Options)
           ?? throw new InvalidDataException("The report could not be deserialized.");
}
