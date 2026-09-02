namespace Plank.Reading.Logical;

public sealed class ParquetReaderOptions
{
    public static ParquetReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    public bool Strict { get; init; } = true;

    public bool VerifyPageCrc { get; init; }

    // RowReaderCore exposes writable refs to its current values and therefore opts out of read-only views.
    internal bool BorrowRequiredPlainValues { get; init; } = true;

    // Generated binary properties are read-only spans, so the row API can point them at array-backed pages.
    internal bool BorrowBinaryValues { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BufferPool);
    }
}
