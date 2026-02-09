namespace Plank.Benchmarks;

public readonly record struct EncodingBenchmarkSizeSnapshot(long ColumnCompressedBytes, long ColumnUncompressedBytes, long FileBytes);
