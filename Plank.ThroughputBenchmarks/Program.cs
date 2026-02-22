using Plank.ThroughputBenchmarks;
using XenoAtom.CommandLine;

const string spacer = "";
var dataDirectory = Path.Combine(AppContext.BaseDirectory, "nyc-data");
var outputDirectory = Path.Combine(AppContext.BaseDirectory, "throughput-output");
string? metricsDirectory = null;
var fileCount = 3;
var iterations = 5;
var keepFiles = false;
var libraries = new List<string>();

var app = new CommandApp("plank-throughput")
{
    spacer,
    { "data-dir=", "Path to source NYC parquet files. Defaults to benchmark nyc-data cache.", v => dataDirectory = v },
    { "out-dir=", "Path for output parquet files.", v => outputDirectory = v },
    { "metrics-dir=", "Path for Plank metrics parquet output (only plank and encode_ahead).", v => metricsDirectory = v },
    { "file-count=", "Number of NYC source files to load (1..5).", (int v) => fileCount = v },
    { "iterations=", "Measured iteration count.", (int v) => iterations = v },
    { "library=", "Library to benchmark. Repeatable. Values: plank, encode_ahead, parquetsharp, parquet.net.", libraries },
    { "keep-files", "Keep generated output files.", _ => keepFiles = true },
    new HelpOption(),
    async _ =>
    {
        if (fileCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(fileCount), "--file-count must be greater than zero.");
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations), "--iterations must be greater than zero.");

        var selectedLibraries = libraries.Count == 0
            ? new[] { "plank", "parquetsharp", "parquet.net" }
            : libraries.Select(static x => x.ToLowerInvariant()).ToArray();

        var manager = new NycDatasetManager();
        var files = await manager.EnsureFilesAsync(dataDirectory, fileCount, CancellationToken.None).ConfigureAwait(false);
        NycBenchmarkContext.SetConfiguration(dataDirectory, fileCount);
        NycBenchmarkContext.Current = manager.LoadContext(files);

        Console.WriteLine($"Loaded {NycBenchmarkContext.Current.TotalRows} rows from {NycBenchmarkContext.Current.SourceFiles.Length} NYC parquet files.");
        Console.WriteLine($"Running throughput benchmark on disk with {iterations} measured iterations.");

        var options = new ThroughputBenchmarkOptions
        {
            OutputDirectory = outputDirectory,
            Libraries = selectedLibraries,
            MeasureIterations = iterations,
            KeepFiles = keepFiles,
            MetricsDirectory = metricsDirectory
        };
        await ThroughputBenchmarkRunner.RunAsync(NycBenchmarkContext.Current, options, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }
};

return await app.RunAsync(args).ConfigureAwait(false);
