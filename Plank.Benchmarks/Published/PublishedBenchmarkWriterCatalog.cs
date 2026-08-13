namespace Plank.Benchmarks.Published;

static class PublishedBenchmarkWriterCatalog
{
    public static IReadOnlyList<IPublishedBenchmarkWriter> Create(PublishedBenchmarkDataSet dataSet, int workerCount)
        =>
        [
            new PlankPublishedBenchmarkWriter(dataSet, 1),
            new PlankPublishedBenchmarkWriter(dataSet, workerCount),
            new ParquetSharpPublishedBenchmarkWriter(dataSet, false, Environment.ProcessorCount),
            new ParquetSharpPublishedBenchmarkWriter(dataSet, true, Environment.ProcessorCount),
            new ParquetNetPublishedBenchmarkWriter(dataSet)
        ];
}
