namespace Plank.Benchmarks.Published;

interface IPublishedBenchmarkReader : IDisposable
{
    string ImplementationId { get; }

    string Label { get; }

    int Threads { get; }

    bool IsSupported { get; }

    string? UnavailableReason { get; }

    ValueTask<PublishedReadResult> ReadAsync(CancellationToken cancellationToken);
}
