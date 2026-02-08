using Plank.Benchmarks;

var dataDirectory = GetArgument(args, "--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "nyc-data");
var outputDirectory = GetArgument(args, "--out-dir") ?? Path.Combine(AppContext.BaseDirectory, "throughput-output");
var metricsDirectory = GetArgument(args, "--metrics-dir");
var fileCount = ParseFileCount(GetArgument(args, "--file-count"), defaultValue: 3);
var warmupIterations = ParsePositiveInt(GetArgument(args, "--warmup"), defaultValue: 1, "--warmup");
var measureIterations = ParsePositiveInt(GetArgument(args, "--iterations"), defaultValue: 5, "--iterations");
var libraries = ParseLibraries(GetArgument(args, "--library"));
var keepFiles = HasFlag(args, "--keep-files");

var manager = new NycDatasetManager();
var files = await manager.EnsureFilesAsync(dataDirectory, fileCount, CancellationToken.None).ConfigureAwait(false);
NycBenchmarkContext.SetConfiguration(dataDirectory, fileCount);
NycBenchmarkContext.Current = manager.LoadContext(files);

Console.WriteLine($"Loaded {NycBenchmarkContext.Current.TotalRows} rows from {NycBenchmarkContext.Current.SourceFiles.Length} NYC parquet files.");
Console.WriteLine($"Running throughput benchmark on disk with {warmupIterations} warmup and {measureIterations} measured iterations.");

var options = new ThroughputBenchmarkOptions
{
    OutputDirectory = outputDirectory,
    Libraries = libraries,
    WarmupIterations = warmupIterations,
    MeasureIterations = measureIterations,
    KeepFiles = keepFiles,
    MetricsDirectory = metricsDirectory
};
await ThroughputBenchmarkRunner.RunAsync(NycBenchmarkContext.Current, options, CancellationToken.None).ConfigureAwait(false);
return;

static string? GetArgument(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(args[i], name))
            continue;
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for argument '{name}'.");
        return args[i + 1];
    }

    return null;
}

static int ParseFileCount(string? value, int defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
        return defaultValue;
    if (!int.TryParse(value, out var parsed))
        throw new ArgumentException($"Invalid --file-count value '{value}'.");
    if (parsed <= 0)
        throw new ArgumentOutOfRangeException(nameof(value), "--file-count must be greater than zero.");
    return parsed;
}

static int ParsePositiveInt(string? value, int defaultValue, string argumentName)
{
    if (string.IsNullOrWhiteSpace(value))
        return defaultValue;
    if (!int.TryParse(value, out var parsed))
        throw new ArgumentException($"Invalid {argumentName} value '{value}'.");
    if (parsed <= 0)
        throw new ArgumentOutOfRangeException(nameof(value), $"{argumentName} must be greater than zero.");
    return parsed;
}

static bool HasFlag(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(args[i], name))
            return true;
    }

    return false;
}

static string[] ParseLibraries(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return ["plank", "parquetsharp", "parquet.net"];

    var items = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (items.Length == 0)
        throw new ArgumentException("Expected at least one --library value.");

    return [.. items.Select(static x => x.ToLowerInvariant())];
}
