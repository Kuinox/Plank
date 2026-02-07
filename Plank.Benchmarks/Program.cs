using BenchmarkDotNet.Running;
using Plank.Benchmarks;

var dataDirectory = GetArgument(args, "--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "nyc-data");
var fileCount = ParseFileCount(GetArgument(args, "--file-count"), defaultValue: 3);

var manager = new NycDatasetManager();
var files = await manager.EnsureFilesAsync(dataDirectory, fileCount, CancellationToken.None).ConfigureAwait(false);
NycBenchmarkContext.SetConfiguration(dataDirectory, fileCount);
NycBenchmarkContext.Current = manager.LoadContext(files);

Console.WriteLine($"Loaded {NycBenchmarkContext.Current.TotalRows} rows from {NycBenchmarkContext.Current.SourceFiles.Length} NYC parquet files.");
BenchmarkRunner.Run<NycParquetWriteBenchmarks>();

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
    return parsed;
}
