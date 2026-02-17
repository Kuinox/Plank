namespace Plank.Tests;

static class ParquetInteropReaders
{
    public static readonly IReadOnlyList<IParquetInteropReader> All =
    [
        new ParquetNetInteropReader(),
        new ParquetSharpInteropReader()
    ];
}
