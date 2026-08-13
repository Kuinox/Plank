namespace Plank.Benchmarks.Published;

public static class PublishedBenchmarkStatistics
{
    public static Summary Summarize(IReadOnlyList<double> samples)
    {
        ArgumentOutOfRangeException.ThrowIfZero(samples.Count);
        var ordered = samples.Order().ToArray();
        var median = Percentile(ordered, 0.5);
        var p25 = Percentile(ordered, 0.25);
        var p75 = Percentile(ordered, 0.75);
        return new Summary(median, p25, p75, median == 0 ? 0 : (p75 - p25) / median * 100);
    }

    public static Winner FindWinner(IReadOnlyList<PublishedBenchmarkReport.Measurement> measurements)
    {
        var available = measurements
            .Where(static result => result.Available && result.Throughput.HasValue)
            .OrderByDescending(static result => result.Throughput)
            .ToArray();
        if (available.Length == 0)
            return new Winner(null, null);

        var plank = available.FirstOrDefault(static result => result.ImplementationId.StartsWith("plank-", StringComparison.Ordinal));
        var competitor = available.FirstOrDefault(static result => !result.ImplementationId.StartsWith("plank-", StringComparison.Ordinal));
        double? speedup = plank?.Throughput is { } plankThroughput && competitor?.Throughput is { } competitorThroughput
            ? plankThroughput / competitorThroughput
            : null;
        return new Winner(available[0].ImplementationId, speedup);
    }

    static double Percentile(double[] ordered, double percentile)
    {
        if (ordered.Length == 1)
            return ordered[0];
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return ordered[lower];
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    public readonly record struct Summary(double Median, double P25, double P75, double VariationPercent);

    public readonly record struct Winner(string? ImplementationId, double? PlankSpeedup);
}
