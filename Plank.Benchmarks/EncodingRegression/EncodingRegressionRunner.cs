using System.Globalization;
using System.Runtime.InteropServices;
using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.EncodingRegression;

static class EncodingRegressionRunner
{
    internal static EncodingRegressionReport Run(EncodingRegressionOptions options, string label, string commit)
    {
        var columns = EncodingRegressionCatalog.Create(options.Rows);
        var results = new List<EncodingRegressionReport.CaseResult>(columns.Count);
        using var stream = new MemoryStream(capacity: 1 << 20);
        foreach (var column in columns)
        {
            Console.WriteLine($"{column.Case.Id} ({column.RowCount:N0} rows)");
            results.Add(RunCase(column, options, stream));
        }

        return new EncodingRegressionReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Label = label,
            Commit = commit,
            Environment = new EncodingRegressionReport.EnvironmentDetails
            {
                RuntimeVersion = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = System.Environment.ProcessorCount
            },
            Configuration = new EncodingRegressionReport.ConfigurationDetails
            {
                Rows = options.Rows,
                Warmups = options.Warmups,
                Iterations = options.Iterations,
                TimingBoundary = "SerializedColumn.Serialize only. That covers level writing, dictionary "
                                 + "construction, value encoding and page splitting, and also the column "
                                 + "statistics pass, which for cheap encodings is a meaningful share of the "
                                 + "measurement and damps encoder-only deltas. The row-group write that "
                                 + "follows is excluded. Compression, page indexes and Bloom filters are off."
            },
            Cases = results
        };
    }

    static EncodingRegressionReport.CaseResult RunCase(IEncodingRegressionColumn column,
        EncodingRegressionOptions options, MemoryStream stream)
    {
        try
        {
            // Audit pass: a complete file, hashed. A pure refactor must not change these bytes.
            var contents = column.WriteCompleteFile();
            var hash = EncodingRegressionColumn<int>.HashFile(contents);

            column.Attach(stream);
            for (var warmup = 0; warmup < options.Warmups; warmup++)
                column.EncodeOnce();

            var expectedLength = column.LastEncodedLength;
            var samples = new List<double>(options.Iterations);
            for (var iteration = 0; iteration < options.Iterations; iteration++)
            {
                var elapsed = column.EncodeOnce();
                if (column.LastEncodedLength != expectedLength)
                    throw new InvalidDataException(
                        $"Encoded length changed between iterations ({expectedLength} then {column.LastEncodedLength}).");
                samples.Add(elapsed.TotalMicroseconds);
            }

            var summary = PublishedBenchmarkStatistics.Summarize(samples);
            var valuesPerSecond = summary.Median > 0 ? column.ValueCount / (summary.Median / 1_000_000d) : (double?)null;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {summary.Median:N1} us  (+/-{summary.VariationPercent:N1}%)  {contents.Length:N0} bytes"));

            return new EncodingRegressionReport.CaseResult
            {
                Id = column.Case.Id,
                DataType = column.Case.DataType,
                Encoding = column.Case.Encoding,
                Repetition = column.Case.Repetition,
                Status = "ok",
                RowCount = column.RowCount,
                ValueCount = column.ValueCount,
                OutputSha256 = hash,
                OutputBytes = contents.Length,
                MedianMicroseconds = summary.Median,
                P25Microseconds = summary.P25,
                P75Microseconds = summary.P75,
                VariationPercent = summary.VariationPercent,
                ValuesPerSecond = valuesPerSecond
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or InvalidDataException)
        {
            Console.WriteLine($"  FAILED: {ex.Message}");
            return new EncodingRegressionReport.CaseResult
            {
                Id = column.Case.Id,
                DataType = column.Case.DataType,
                Encoding = column.Case.Encoding,
                Repetition = column.Case.Repetition,
                Status = "failed",
                Error = ex.Message,
                RowCount = column.RowCount,
                ValueCount = column.ValueCount
            };
        }
    }
}

sealed class EncodingRegressionOptions
{
    public int Rows { get; init; } = 200_000;

    public int Warmups { get; init; } = 3;

    public int Iterations { get; init; } = 15;
}
