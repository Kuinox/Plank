namespace Plank.Benchmarks;

public sealed class BenchmarkDataContext
{
    public required string[] SourceFiles { get; init; }

    public required NycTripData[] TripsByFile { get; init; }

    public required int TotalRows { get; init; }
}
