namespace Plank.Tests.E2E.Interop;

sealed class ParquetRowGroupReadResult
{
    public required int[] Int32Values { get; init; }

    public required long[] Int64Values { get; init; }

    public required double[] DoubleValues { get; init; }

    public required byte[][] BinaryValues { get; init; }
}
