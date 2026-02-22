namespace Plank.ThroughputBenchmarks;

public sealed class ThroughputBenchmarkOptions
{
    public required string OutputDirectory { get; init; }

    public required string[] Libraries { get; init; }

    public required int MeasureIterations { get; init; }

    public required bool KeepFiles { get; init; }

    public required string? MetricsDirectory { get; init; }
}
