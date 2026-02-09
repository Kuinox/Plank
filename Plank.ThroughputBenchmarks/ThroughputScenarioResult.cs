namespace Plank.Benchmarks;

public sealed class ThroughputScenarioResult
{
    public required string Name { get; init; }

    public required int Iterations { get; init; }

    public required long TotalBytes { get; init; }

    public required double AverageMegabytesPerSecond { get; init; }

    public required double AverageRowsPerSecond { get; init; }

    public required TimeSpan AverageElapsed { get; init; }
}
