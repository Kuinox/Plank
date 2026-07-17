namespace Plank.RowApi;

using Plank.Writing;

public sealed class RowReaderOptions
{
    public static RowReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    public ParquetExecutionOptions Execution { get; init; } = new();

    public int MaxReadAheadRowGroups { get; init; } = 1;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BufferPool);
        ArgumentNullException.ThrowIfNull(Execution);
        Execution.Validate();
        if (MaxReadAheadRowGroups < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxReadAheadRowGroups), MaxReadAheadRowGroups,
                "Max read-ahead row groups must be non-negative.");
        if (MaxReadAheadRowGroups > 1)
            throw new ArgumentOutOfRangeException(nameof(MaxReadAheadRowGroups), MaxReadAheadRowGroups,
                "Read-ahead is intentionally limited to one row group for backpressure.");
    }
}
