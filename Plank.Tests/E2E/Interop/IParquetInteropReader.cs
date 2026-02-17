namespace Plank.Tests;

interface IParquetInteropReader
{
    string Name { get; }

    Task<ParquetFileReadResult> ReadExpectedSchemaAsync(string path);
}
