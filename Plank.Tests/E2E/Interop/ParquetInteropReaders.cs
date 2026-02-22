namespace Plank.Tests.E2E.Interop;

static class ParquetInteropReaders
{
    public static readonly IReadOnlyList<IParquetInteropReader> All =
    [
        new ParquetNetInteropReader(),
        new ParquetSharpInteropReader()
    ];
}
