namespace Plank.Benchmarks;

public sealed class BenchmarkDataContext
{
    public required string[] SourceFiles { get; init; }

    public required int[][] VendorIdsByFile { get; init; }

    public required int TotalRows { get; init; }
}
