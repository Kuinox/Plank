using Plank.Writing;

namespace Plank.Reading.Physical;

public sealed class ParquetFileReaderOptions
{
    public static ParquetFileReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    internal void Validate()
        => ArgumentNullException.ThrowIfNull(BufferPool);
}
