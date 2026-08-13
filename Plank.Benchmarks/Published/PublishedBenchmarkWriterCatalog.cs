namespace Plank.Benchmarks.Published;

static class PublishedBenchmarkWriterCatalog
{
    public static IReadOnlyList<IPublishedBenchmarkWriter> Create(PublishedBenchmarkDataSet dataSet, int workerCount)
    {
        var plankValues = PlankPublishedBenchmarkWriter.PrepareValues(dataSet);
        return
        [
            new PlankPublishedBenchmarkWriter(dataSet, 1, plankValues),
            new PlankPublishedBenchmarkWriter(dataSet, workerCount, plankValues),
            new ParquetSharpPublishedBenchmarkWriter(dataSet, false, Environment.ProcessorCount),
            new ParquetSharpPublishedBenchmarkWriter(dataSet, true, Environment.ProcessorCount),
            new ParquetNetPublishedBenchmarkWriter(dataSet)
        ];
    }
}
