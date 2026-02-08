namespace Plank.Benchmarks;

public readonly record struct FlushMetricSample(
    int Index,
    long FlushDurationTicks,
    long FlushGapTicks,
    long CumulativeTicksAfterFlush);
