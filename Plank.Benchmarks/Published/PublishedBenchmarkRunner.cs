using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Benchmarks.Published;

public static class PublishedBenchmarkRunner
{
    public static async Task<PublishedBenchmarkReport> RunAsync(
        IReadOnlyList<PublishedBenchmarkDataSet> realWorldData,
        IReadOnlyList<PublishedBenchmarkDataSet> syntheticData,
        PublishedBenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        ValidateCaseId(realWorldData, syntheticData, options.CaseId);
        using var stream = new NonClosingMemoryStream();
        var suites = new List<PublishedBenchmarkReport.SuiteResult>(2)
        {
            await RunSuiteAsync("real-world", "Real-world data", realWorldData, options, stream, cancellationToken)
                .ConfigureAwait(false),
            await RunSuiteAsync("synthetic", "Synthetic", syntheticData, options, stream, cancellationToken)
                .ConfigureAwait(false)
        };
        return new PublishedBenchmarkReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Environment = CreateEnvironmentDetails(),
            Configuration = new PublishedBenchmarkReport.ConfigurationDetails
            {
                Warmups = options.Warmups,
                Iterations = options.Iterations,
                Compression = "none",
                DataPageVersion = "V1",
                PageIndexes = false,
                BloomFilters = false,
                RowGroupBoundaries = "Identical for every writer and preserved from the source taxi file.",
                TimingBoundary = "Writer creation through complete in-memory output, metadata, footer, and close. " +
                                 "Input loading is excluded. Timestamp columns are handed to every writer as logical " +
                                 "DateTime values, so the conversion each library performs is timed. String columns " +
                                 "are handed to Plank and ParquetSharp pre-encoded; Parquet.Net has no pre-encoded " +
                                 "entry point and encodes inside the timed region.",
                Quick = options.Quick
            },
            Suites = suites
        };
    }

    static async Task<PublishedBenchmarkReport.SuiteResult> RunSuiteAsync(string id, string label,
        IReadOnlyList<PublishedBenchmarkDataSet> dataSets, PublishedBenchmarkOptions options,
        NonClosingMemoryStream stream, CancellationToken cancellationToken)
    {
        var results = new List<PublishedBenchmarkReport.CaseResult>(dataSets.Count);
        foreach (var dataSet in dataSets)
        {
            if (options.CaseId is { } caseId && !string.Equals(dataSet.Id, caseId, StringComparison.Ordinal))
                continue;
            Console.WriteLine($"{label}: {dataSet.Label} ({dataSet.RowCount:N0} rows, {dataSet.Columns.Count} columns)");
            results.Add(await RunCaseAsync(dataSet, options, stream, cancellationToken).ConfigureAwait(false));
        }
        return new PublishedBenchmarkReport.SuiteResult { Id = id, Label = label, Cases = results };
    }

    static async Task<PublishedBenchmarkReport.CaseResult> RunCaseAsync(PublishedBenchmarkDataSet dataSet,
        PublishedBenchmarkOptions options, NonClosingMemoryStream stream, CancellationToken cancellationToken)
    {
        var writers = PublishedBenchmarkWriterCatalog.Create(dataSet, options.WorkerCount);
        try
        {
            var outputSizes = new long?[writers.Count];
            for (var writerIndex = 0; writerIndex < writers.Count; writerIndex++)
                if (writers[writerIndex].IsSupported)
                    outputSizes[writerIndex] = await PublishedBenchmarkAuditor.WriteAndValidateAsync(
                        writers[writerIndex], dataSet, stream, cancellationToken).ConfigureAwait(false);

            for (var warmup = 0; warmup < options.Warmups; warmup++)
                foreach (var writerIndex in RotatedOrder(writers.Count, warmup))
                    if (writers[writerIndex].IsSupported)
                        await WriteOnceAsync(writers[writerIndex], stream, cancellationToken).ConfigureAwait(false);

            var samples = Enumerable.Range(0, writers.Count).Select(static _ => new List<double>()).ToArray();
            for (var iteration = 0; iteration < options.Iterations; iteration++)
                foreach (var writerIndex in RotatedOrder(writers.Count, iteration))
                {
                    var writer = writers[writerIndex];
                    if (!writer.IsSupported)
                        continue;
                    var elapsed = await WriteOnceAsync(writer, stream, cancellationToken).ConfigureAwait(false);
                    if (stream.Length != outputSizes[writerIndex])
                        throw new InvalidDataException($"{writer.Label} output size changed between iterations.");
                    samples[writerIndex].Add(elapsed.TotalMilliseconds);
                    Console.WriteLine($"  {writer.Label}: {elapsed.TotalMilliseconds:N2} ms");
                }

            var measurements = new PublishedBenchmarkReport.Measurement[writers.Count];
            for (var writerIndex = 0; writerIndex < writers.Count; writerIndex++)
                measurements[writerIndex] = CreateMeasurement(writers[writerIndex], samples[writerIndex],
                    outputSizes[writerIndex], dataSet);
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
            foreach (var writer in writers)
                writer.Dispose();
        }
    }

    static PublishedBenchmarkReport.Measurement CreateMeasurement(IPublishedBenchmarkWriter writer,
        IReadOnlyList<double> samples, long? outputBytes, PublishedBenchmarkDataSet dataSet)
    {
        if (!writer.IsSupported)
            return new PublishedBenchmarkReport.Measurement
            {
                ImplementationId = writer.ImplementationId,
                Label = writer.Label,
                Threads = writer.Threads,
                Available = false,
                UnavailableReason = writer.UnavailableReason
            };

        var summary = PublishedBenchmarkStatistics.Summarize(samples);
        var units = dataSet.SuiteId == "synthetic" ? dataSet.ValueCount : dataSet.RowCount;
        return new PublishedBenchmarkReport.Measurement
        {
            ImplementationId = writer.ImplementationId,
            Label = writer.Label,
            Threads = writer.Threads,
            Available = true,
            MedianMilliseconds = summary.Median,
            P25Milliseconds = summary.P25,
            P75Milliseconds = summary.P75,
            VariationPercent = summary.VariationPercent,
            Throughput = units / (summary.Median / 1_000) / 1_000_000,
            OutputBytes = outputBytes,
            SamplesMilliseconds = samples.ToArray()
        };
    }

    static async Task<TimeSpan> WriteOnceAsync(IPublishedBenchmarkWriter writer, NonClosingMemoryStream stream,
        CancellationToken cancellationToken)
    {
        writer.PrepareWrite();
        stream.Reset();
        var started = Stopwatch.GetTimestamp();
        await writer.WriteAsync(stream, cancellationToken).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(started);
    }

    internal static IEnumerable<int> RotatedOrder(int count, int rotation)
    {
        for (var index = 0; index < count; index++)
            yield return (index + rotation) % count;
    }

    internal static PublishedBenchmarkReport.EnvironmentDetails CreateEnvironmentDetails()
        => new()
        {
            Cpu = ReadCpuName(),
            LogicalProcessors = Environment.ProcessorCount,
            OperatingSystem = RuntimeInformation.OSDescription,
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            Commit = ReadCommit(),
            Libraries = new Dictionary<string, string>
            {
                ["Plank"] = ReadVersion(typeof(ParquetSchema).Assembly),
                ["ParquetSharp"] = ReadVersion(typeof(ParquetSharp.ParquetFileWriter).Assembly),
                ["Parquet.Net"] = ReadVersion(typeof(Parquet.ParquetWriter).Assembly),
                ["Apache.Arrow"] = ReadVersion(typeof(Apache.Arrow.RecordBatch).Assembly)
            }
        };

    static string ReadCpuName()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            var model = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model is not null)
                return model[(model.IndexOf(':') + 1)..].Trim();
        }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    // The "-dirty" suffix used to be unconditional, which made it meaningless.
    // ReadProcess turns *any* empty output into the string "unknown", and a clean
    // tree is exactly the case where `git status --porcelain` prints nothing — so
    // status came back as "unknown", the emptiness test below failed, and every
    // snapshot was stamped dirty no matter what. Verified by sampling git status
    // every five seconds across a whole run: 75 consecutive clean samples, still
    // stamped dirty.
    //
    // So this reads the status itself instead of going through the sentinel, and
    // distinguishes the three outcomes that matter: git unavailable or failing
    // (unknown), clean (bare commit), modified (suffixed).
    static string ReadCommit()
    {
        try
        {
            var commit = RunGit("rev-parse HEAD");
            if (commit is "unknown" or "")
                return "unknown";
            if (!TryRunGitRaw("status --porcelain --untracked-files=no", out var status))
                return "unknown";
            return status.Length == 0 ? commit : $"{commit}-dirty";
        }
        catch
        {
            return "unknown";
        }
    }

    // Like RunGit, but keeps empty output as empty rather than collapsing it to a
    // sentinel, and reports process failure separately.
    static bool TryRunGitRaw(string arguments, out string output)
    {
        output = "";
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
            return false;

        output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    static string RunGit(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return process is null ? "unknown" : ReadProcess(process);
    }

    static string ReadProcess(Process process)
    {
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && value.Length != 0 ? value : "unknown";
    }

    static string ReadVersion(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? assembly.GetName().Version?.ToString()
           ?? "unknown";

    internal static void ValidateOptions(PublishedBenchmarkOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.Warmups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Iterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.WorkerCount);
    }

    internal static void ValidateCaseId(IReadOnlyList<PublishedBenchmarkDataSet> realWorldData,
        IReadOnlyList<PublishedBenchmarkDataSet> syntheticData, string? caseId)
    {
        if (caseId is null || realWorldData.Any(dataSet => dataSet.Id == caseId) ||
            syntheticData.Any(dataSet => dataSet.Id == caseId))
            return;

        throw new ArgumentException($"Unknown published benchmark case '{caseId}'.", nameof(caseId));
    }
}
