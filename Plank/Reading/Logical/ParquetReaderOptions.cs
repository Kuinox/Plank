namespace Plank.Reading.Logical;

using Plank.Writing;

public sealed class ParquetReaderOptions
{
    public static ParquetReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    public ParquetExecutionOptions Execution { get; init; } = new();

    public int MaxReadAheadRowGroups { get; init; } = 1;

    public bool Strict { get; init; } = true;

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
