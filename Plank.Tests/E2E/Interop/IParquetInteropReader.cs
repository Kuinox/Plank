namespace Plank.Tests.E2E.Interop;

interface IParquetInteropReader
{
    string Name { get; }

    Task<ParquetFileReadResult> ReadExpectedSchemaAsync(string path);
}
