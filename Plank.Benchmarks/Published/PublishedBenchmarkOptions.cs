namespace Plank.Benchmarks.Published;

public sealed class PublishedBenchmarkOptions
{
    public int Warmups { get; init; } = 8;

    public int Iterations { get; init; } = 7;

    public bool Quick { get; init; }

    public int WorkerCount { get; init; } = Environment.ProcessorCount;

    public int SyntheticRows { get; init; } = 1_000_000;

    public int SyntheticWidth { get; init; } = Environment.ProcessorCount;

    public int QuickRows { get; init; } = 4_096;

    public int QuickWidth { get; init; } = Math.Min(4, Environment.ProcessorCount);

    public string? CaseId { get; init; }
}
