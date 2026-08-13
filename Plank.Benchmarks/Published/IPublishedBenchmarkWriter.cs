namespace Plank.Benchmarks.Published;

interface IPublishedBenchmarkWriter : IDisposable
{
    string ImplementationId { get; }

    string Label { get; }

    int Threads { get; }

    bool IsSupported { get; }

    string? UnavailableReason { get; }

    void PrepareWrite();

    ValueTask WriteAsync(Stream destination, CancellationToken cancellationToken);
}
