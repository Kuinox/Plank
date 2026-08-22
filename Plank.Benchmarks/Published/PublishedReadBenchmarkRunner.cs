using System.Diagnostics;

namespace Plank.Benchmarks.Published;

public static class PublishedReadBenchmarkRunner
{
    public static async Task<PublishedBenchmarkReport> RunAsync(
        IReadOnlyList<PublishedBenchmarkDataSet> realWorldData,
        IReadOnlyList<PublishedBenchmarkDataSet> syntheticData,
        PublishedBenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        PublishedBenchmarkRunner.ValidateOptions(options);
        PublishedBenchmarkRunner.ValidateCaseId(realWorldData, syntheticData, options.CaseId);
        var suites = new List<PublishedBenchmarkReport.SuiteResult>(2)
        {
            await RunSuiteAsync("real-world", "Real-world data", realWorldData, options, cancellationToken)
                .ConfigureAwait(false),
            await RunSuiteAsync("synthetic", "Synthetic", syntheticData, options, cancellationToken)
                .ConfigureAwait(false)
        };
        return new PublishedBenchmarkReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Environment = PublishedBenchmarkRunner.CreateEnvironmentDetails(),
            Configuration = new PublishedBenchmarkReport.ConfigurationDetails
            {
                Warmups = options.Warmups,
                Iterations = options.Iterations,
                Compression = "none",
                DataPageVersion = "V1",
                PageIndexes = false,
                BloomFilters = false,
                RowGroupBoundaries = "Every reader uses the same in-memory file for each case.",
                TimingBoundary = "Reader creation through footer parsing, decoding, and additive consumption " +
                    "of every logical value. Variable-length values contribute their byte length. Input file " +
                    "generation is excluded.",
                Quick = options.Quick
            },
            Suites = suites
        };
    }

    static async Task<PublishedBenchmarkReport.SuiteResult> RunSuiteAsync(string id, string label,
        IReadOnlyList<PublishedBenchmarkDataSet> dataSets, PublishedBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<PublishedBenchmarkReport.CaseResult>(dataSets.Count);
        foreach (var dataSet in dataSets)
        {
            if (options.CaseId is { } caseId && !string.Equals(dataSet.Id, caseId, StringComparison.Ordinal))
                continue;
            Console.WriteLine($"{label}: {dataSet.Label} ({dataSet.RowCount:N0} rows, {dataSet.Columns.Count} columns)");
            results.Add(await RunCaseAsync(dataSet, options, cancellationToken).ConfigureAwait(false));
        }
        return new PublishedBenchmarkReport.SuiteResult { Id = id, Label = label, Cases = results };
    }

    static async Task<PublishedBenchmarkReport.CaseResult> RunCaseAsync(PublishedBenchmarkDataSet dataSet,
        PublishedBenchmarkOptions options, CancellationToken cancellationToken)
    {
        var fileBytes = await CreateInputAsync(dataSet, cancellationToken).ConfigureAwait(false);
        var expected = PublishedReadChecksum.Expected(dataSet);
        var readers = PublishedBenchmarkReaderCatalog.Create(fileBytes, dataSet, options.WorkerCount);
        try
        {
            for (var readerIndex = 0; readerIndex < readers.Count; readerIndex++)
                if (readers[readerIndex].IsSupported)
                    ValidateResult(readers[readerIndex].Label, expected,
                        await readers[readerIndex].ReadAsync(cancellationToken).ConfigureAwait(false));

            for (var warmup = 0; warmup < options.Warmups; warmup++)
                foreach (var readerIndex in PublishedBenchmarkRunner.RotatedOrder(readers.Count, warmup))
                    if (readers[readerIndex].IsSupported)
                        _ = await readers[readerIndex].ReadAsync(cancellationToken).ConfigureAwait(false);

            var samples = Enumerable.Range(0, readers.Count).Select(static _ => new List<double>()).ToArray();
            for (var iteration = 0; iteration < options.Iterations; iteration++)
                foreach (var readerIndex in PublishedBenchmarkRunner.RotatedOrder(readers.Count, iteration))
                {
                    if (!readers[readerIndex].IsSupported)
                        continue;
                    var elapsed = await ReadOnceAsync(readers[readerIndex], expected, cancellationToken)
                        .ConfigureAwait(false);
                    samples[readerIndex].Add(elapsed.TotalMilliseconds);
                    Console.WriteLine($"  {readers[readerIndex].Label}: {elapsed.TotalMilliseconds:N2} ms");
                }

            var measurements = new PublishedBenchmarkReport.Measurement[readers.Count];
            for (var readerIndex = 0; readerIndex < readers.Count; readerIndex++)
                measurements[readerIndex] = CreateMeasurement(readers[readerIndex], samples[readerIndex], dataSet);
            var winner = PublishedBenchmarkStatistics.FindWinner(measurements);
            return new PublishedBenchmarkReport.CaseResult
            {
                Id = dataSet.Id,
                Label = dataSet.Label,
                Encoding = dataSet.Encoding,
                DataTypes = dataSet.DataTypes,
                RowCount = dataSet.RowCount,
                ValueCount = dataSet.ValueCount,
                ColumnCount = dataSet.Columns.Count,
                ThroughputUnit = dataSet.ThroughputUnit,
                Measurements = measurements,
                WinnerId = winner.ImplementationId,
                PlankSpeedup = winner.PlankSpeedup
            };
        }
        finally
        {
            foreach (var reader in readers)
                reader.Dispose();
        }
    }

    static async Task<byte[]> CreateInputAsync(PublishedBenchmarkDataSet dataSet,
        CancellationToken cancellationToken)
    {
        using var stream = new NonClosingMemoryStream();
        using var writer = new PlankPublishedBenchmarkWriter(dataSet, 1);
        writer.PrepareWrite();
        await writer.WriteAsync(stream, cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }

    static PublishedBenchmarkReport.Measurement CreateMeasurement(IPublishedBenchmarkReader reader,
        IReadOnlyList<double> samples, PublishedBenchmarkDataSet dataSet)
    {
        if (!reader.IsSupported)
            return new PublishedBenchmarkReport.Measurement
            {
                ImplementationId = reader.ImplementationId,
                Label = reader.Label,
                Threads = reader.Threads,
                Available = false,
                UnavailableReason = reader.UnavailableReason
            };

        var summary = PublishedBenchmarkStatistics.Summarize(samples);
        var units = dataSet.SuiteId == "synthetic" ? dataSet.ValueCount : dataSet.RowCount;
        return new PublishedBenchmarkReport.Measurement
        {
            ImplementationId = reader.ImplementationId,
            Label = reader.Label,
            Threads = reader.Threads,
            Available = true,
            MedianMilliseconds = summary.Median,
            P25Milliseconds = summary.P25,
            P75Milliseconds = summary.P75,
            VariationPercent = summary.VariationPercent,
            Throughput = units / (summary.Median / 1_000) / 1_000_000,
            SamplesMilliseconds = samples.ToArray()
        };
    }

    static async Task<TimeSpan> ReadOnceAsync(IPublishedBenchmarkReader reader, PublishedReadResult expected,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var actual = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        ValidateResult(reader.Label, expected, actual);
        return elapsed;
    }

    static void ValidateResult(string label, PublishedReadResult expected, PublishedReadResult actual)
    {
        if (actual != expected)
            throw new InvalidDataException(
                $"{label} returned {actual.ValueCount} values with checksum {actual.Checksum:X16}; " +
                $"expected {expected.ValueCount} values with checksum {expected.Checksum:X16}.");
    }
}
