namespace Plank.RowApi;

public sealed class RowReaderOptions
{
    public static RowReaderOptions Default { get; } = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    /// <summary>Controls decoding workers. One worker uses the calling thread; larger values enable background workers, capped by projected columns.</summary>
    public ParquetExecutionOptions Execution { get; init; } = new();

    /// <summary>Gets or initializes the maximum number of future row groups to prefetch: zero or one.</summary>
    /// <remarks>With background workers, one prepares the first value buffer of each projected column in the next nonempty row group. Single-worker reading does not prefetch.</remarks>
    public int MaxReadAheadRowGroups { get; init; } = 1;

    public bool VerifyPageCrc { get; init; }

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
