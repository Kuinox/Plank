namespace Plank.Reading.Logical;

public sealed class ParquetReaderOptions
{
    public static ParquetReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    public bool Strict { get; init; } = true;

    public bool VerifyPageCrc { get; init; }

    // RowReaderCore exposes writable refs to its current values and therefore opts out of read-only views.
    internal bool BorrowRequiredPlainInt32Values { get; init; } = true;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BufferPool);
    }
}
