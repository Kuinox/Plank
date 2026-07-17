namespace Plank.Reading.Logical;

using Plank.Writing;

public sealed class ParquetReaderOptions
{
    public static ParquetReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    public bool Strict { get; init; } = true;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BufferPool);
    }
}
