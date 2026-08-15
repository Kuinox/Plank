using System.Globalization;
using System.Runtime.InteropServices;
using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.EncodingRegression;

static class EncodingRegressionRunner
{
    internal static EncodingRegressionReport Run(EncodingRegressionOptions options, string label, string commit)
    {
        var columns = EncodingRegressionCatalog.Create(options.Rows);
        using var stream = new MemoryStream(capacity: 1 << 20);

        // Audit pass: one complete file per case, hashed. A pure refactor must not change these bytes.
        var states = new CaseState[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            states[i] = CaseState.Audit(columns[i], stream, options.Iterations);

        // Round-robin: every case is measured once per round rather than all its iterations back to
        // back. On a contended runner a slow window would otherwise land entirely inside one case and
        // move that case's minimum; spreading each case's samples across the whole run makes the
        // per-case minimum reflect the code rather than when the case happened to be scheduled.
        for (var warmup = 0; warmup < options.Warmups; warmup++)
            foreach (var state in states)
                state.Warmup();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        for (var round = 0; round < options.Iterations; round++)
        {
            foreach (var state in states)
                state.Measure();
            if ((round + 1) % 10 == 0)
                Console.WriteLine($"round {round + 1}/{options.Iterations}");
        }

        var results = new List<EncodingRegressionReport.CaseResult>(states.Length);
        foreach (var state in states)
            results.Add(state.ToResult());

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
                                 + "follows is excluded. Compression, page indexes and Bloom filters are off. "
                                 + "Cases are measured round-robin, one sample each per round."
            },
            Cases = results
        };
    }

    sealed class CaseState
    {
        readonly IEncodingRegressionColumn _column;
        readonly List<double> _samples;
        readonly string? _hash;
        readonly long _outputBytes;
        long _expectedLength = -1;
        string? _error;

        CaseState(IEncodingRegressionColumn column, int iterations, string? hash, long outputBytes, string? error)
        {
            _column = column;
            _samples = new List<double>(iterations);
            _hash = hash;
            _outputBytes = outputBytes;
            _error = error;
        }

        internal static CaseState Audit(IEncodingRegressionColumn column, MemoryStream stream, int iterations)
        {
            try
            {
                var contents = column.WriteCompleteFile();
                var hash = EncodingRegressionColumn<int>.HashFile(contents);
                column.Attach(stream);
                return new CaseState(column, iterations, hash, contents.Length, error: null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or InvalidDataException)
            {
                Console.WriteLine($"{column.Case.Id}: FAILED: {ex.Message}");
                return new CaseState(column, iterations, hash: null, outputBytes: 0, ex.Message);
            }
        }

        internal void Warmup()
        {
            if (_error is not null)
                return;
            try
            {
                _column.EncodeOnce();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                _error = ex.Message;
            }
        }

        internal void Measure()
        {
            if (_error is not null)
                return;
            try
            {
                var elapsed = _column.EncodeOnce();
                if (_expectedLength < 0)
                    _expectedLength = _column.LastEncodedLength;
                else if (_column.LastEncodedLength != _expectedLength)
                    throw new InvalidDataException(
                        $"Encoded length changed between iterations ({_expectedLength} then {_column.LastEncodedLength}).");
                _samples.Add(elapsed.TotalMicroseconds);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or InvalidDataException)
            {
                _error = ex.Message;
            }
        }

        internal EncodingRegressionReport.CaseResult ToResult()
        {
            if (_error is not null || _samples.Count == 0)
                return new EncodingRegressionReport.CaseResult
                {
                    Id = _column.Case.Id,
                    DataType = _column.Case.DataType,
                    Encoding = _column.Case.Encoding,
                    Repetition = _column.Case.Repetition,
                    Status = "failed",
                    Error = _error ?? "No samples were collected.",
                    RowCount = _column.RowCount,
                    ValueCount = _column.ValueCount
                };

            var summary = PublishedBenchmarkStatistics.Summarize(_samples);
            var minimum = _samples.Min();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{_column.Case.Id,-46} min {minimum,10:N1} us  median {summary.Median,10:N1} us  {_outputBytes,12:N0} bytes"));

            return new EncodingRegressionReport.CaseResult
            {
                Id = _column.Case.Id,
                DataType = _column.Case.DataType,
                Encoding = _column.Case.Encoding,
                Repetition = _column.Case.Repetition,
                Status = "ok",
                RowCount = _column.RowCount,
                ValueCount = _column.ValueCount,
                OutputSha256 = _hash,
                OutputBytes = _outputBytes,
                MinMicroseconds = minimum,
                MedianMicroseconds = summary.Median,
                P25Microseconds = summary.P25,
                P75Microseconds = summary.P75,
                VariationPercent = summary.VariationPercent,
                ValuesPerSecond = minimum > 0 ? _column.ValueCount / (minimum / 1_000_000d) : null
            };
        }
    }
}

sealed class EncodingRegressionOptions
{
    public int Rows { get; init; } = 200_000;

    public int Warmups { get; init; } = 3;

    public int Iterations { get; init; } = 30;
}
