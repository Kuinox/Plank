namespace Plank.Reading;

using Plank.Writing;

public sealed class ParquetReaderOptions
{
    public static ParquetReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    internal void Validate()
        => ArgumentNullException.ThrowIfNull(BufferPool);
}
