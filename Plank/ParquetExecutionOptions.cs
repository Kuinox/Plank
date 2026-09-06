namespace Plank;

public sealed class ParquetExecutionOptions
{
    public int WorkerCount { get; init; } = Environment.ProcessorCount;

    /// <summary>Gets or initializes the callback invoked once when each reader or writer worker thread starts.</summary>
    public Action<ParquetWorkerContext>? OnWorkerStarted { get; init; }

    internal void Validate()
    {
        if (WorkerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(WorkerCount), WorkerCount,
                "Worker count must be greater than zero.");
    }
}
