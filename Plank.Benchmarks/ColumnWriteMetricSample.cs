namespace Plank.Benchmarks;

public readonly record struct ColumnWriteMetricSample(
    int Index,
    string ColumnName,
    int RowCount,
    int ValueCount,
    int BytesWritten,
    long EncodeTicks,
    long CompressTicks,
    long WaitForWriteTicks,
    long WriteTicks,
    double StartMs,
    double EndMs);
