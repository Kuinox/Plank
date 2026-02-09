namespace Plank.Benchmarks;

public readonly record struct StreamWriteMetricSample(
    int Index,
    int ByteCount,
    long WriteGapTicks,
    long WriteDurationTicks,
    long CumulativeTicksAfterWrite);
