namespace Plank.Benchmarks;

public readonly record struct StringEncodingMetricSample(
    int Index,
    string ColumnName,
    int RowCount,
    int NonNullCount,
    long SizePassTicks,
    long DefinitionLevelsTicks,
    long ByteCountPassTicks,
    long Utf8WritePassTicks);
