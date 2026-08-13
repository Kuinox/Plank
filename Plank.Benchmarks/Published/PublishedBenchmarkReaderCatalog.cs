namespace Plank.Benchmarks.Published;

static class PublishedBenchmarkReaderCatalog
{
    public static IReadOnlyList<IPublishedBenchmarkReader> Create(byte[] fileBytes,
        PublishedBenchmarkDataSet dataSet, int workerCount)
        =>
        [
            new PlankPublishedBenchmarkReader(fileBytes, dataSet, 1),
            new PlankPublishedBenchmarkReader(fileBytes, dataSet, workerCount),
            new ParquetSharpPublishedBenchmarkReader(fileBytes, dataSet, false, workerCount),
            new ParquetSharpPublishedBenchmarkReader(fileBytes, dataSet, true, workerCount),
            new ParquetNetPublishedBenchmarkReader(fileBytes, dataSet)
        ];
}
