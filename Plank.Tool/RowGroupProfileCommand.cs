using Plank.Tool.Querying;
using ConsoleAppFramework;

namespace Plank.Tool;

internal static class RowGroupProfileCommand
{
    /// <summary>Repack pages and profile native footers or SQL requests through the local S3 simulator.</summary>
    /// <param name="file">Input Parquet file.</param>
    /// <param name="sizes">Comma-separated target row counts per row group.</param>
    /// <param name="engines">Comma-separated engines: duckdb, polars, datafusion, clickhouse, spark.</param>
    /// <param name="output">Result path; defaults to INPUT.rowgroups-profile.parquet, or INPUT.rowgroups-requests.json with an adjacent HTML report when a query is given.</param>
    /// <param name="variants">Directory for generated files; defaults to OUTPUT without its extension.</param>
    /// <param name="iterations">Measured inspections or queries per engine and variant.</param>
    /// <param name="warmup">Untimed inspections or queries before measurements.</param>
    /// <param name="threads">Engine thread count.</param>
    /// <param name="query">Optional SQL using the data table; produces JSON traces and an HTML report through the local S3 simulator.</param>
    /// <param name="latency">Simulator latency before each response, in milliseconds.</param>
    /// <param name="initialThroughput">Initial shared simulator throughput, in MiB/s.</param>
    /// <param name="rampUpTime">Linear ramp duration after the first payload byte, in milliseconds.</param>
    /// <param name="maxThroughput">Maximum shared simulator throughput, in MiB/s.</param>
    /// <param name="workerDirectory">Directory for the automatically managed Python project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int> Run([Argument] string file, string sizes = "10000,100000,1000000",
        string engines = "duckdb,polars,datafusion", string? output = null, string? variants = null,
        int iterations = 50, int warmup = 5, int threads = 1, string? query = null,
        double latency = 100, double initialThroughput = 1.25, double rampUpTime = 700, double maxThroughput = 53,
        string? workerDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        string? resultPath = null;
        string? variantPath = null;
        try
        {
            file = Path.GetFullPath(file);
            if (!File.Exists(file)) throw new FileNotFoundException("Input Parquet file not found.", file);
            var selected = PythonProject.Engines(engines);
            var targets = sizes.Split(',', StringSplitOptions.TrimEntries).Select(long.Parse).Distinct().Order().ToArray();
            if (targets.Length == 0 || targets.Any(x => x <= 0)) throw new ArgumentException("Sizes must be positive row counts.");
            if (iterations <= 0 || warmup < 0 || threads <= 0) throw new ArgumentException("Iterations and threads must be positive; warmup cannot be negative.");
            if (query is not null)
            {
                if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query cannot be empty.");
                if (selected.Contains("spark")) throw new ArgumentException("Simulated HTTP queries support duckdb, polars, datafusion and clickhouse.");
                if (!double.IsFinite(latency) || latency < 0 || !double.IsFinite(rampUpTime) || rampUpTime < 0 ||
                    !double.IsFinite(initialThroughput) || initialThroughput <= 0 ||
                    !double.IsFinite(maxThroughput) || maxThroughput < initialThroughput)
                    throw new ArgumentException("Latency and ramp time must be nonnegative; throughputs must be positive and initial <= maximum.");
            }
            output = Path.GetFullPath(output ?? Path.ChangeExtension(file, query is null ? ".rowgroups-profile.parquet" : ".rowgroups-requests.json"));
            if (query is not null && (File.Exists(Path.ChangeExtension(output, ".html")) || Directory.Exists(Path.ChangeExtension(output, ".html")) || Path.ChangeExtension(output, ".html") == file || Path.ChangeExtension(output, ".html") == output))
                throw new IOException("Report output already exists or aliases the input/result.");
            variants = Path.GetFullPath(variants ?? Path.ChangeExtension(output, null));
            if (Path.GetFullPath(file) == output || File.Exists(output) || Directory.Exists(output))
                throw new IOException("Result output already exists or aliases the input.");
            if (Directory.Exists(variants) || File.Exists(variants)) throw new IOException("Variant directory already exists.");
            if (!Directory.Exists(Path.GetDirectoryName(output))) throw new DirectoryNotFoundException("Result parent directory does not exist.");
            resultPath = output;
            variantPath = variants;
            var project = await PythonProject.Ensure(workerDirectory, selected, cancellationToken);
            var path = await project.Run(new { kind = "rowgroups", run_id = runId, file, sizes = targets, engines = selected,
                output, variants, iterations, warmup, threads, query,
                simulation = new { latency_ms = latency, initial_throughput_mib_s = initialThroughput, ramp_up_ms = rampUpTime, max_throughput_mib_s = maxThroughput } }, cancellationToken);
            Console.WriteLine(path.Trim());
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Profiling cancelled."); return 130; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally
        {
            if (variantPath is not null && resultPath is not null && !File.Exists(resultPath))
            {
                var marker = Path.Combine(variantPath, ".plank-run-id");
                try
                {
                    if (File.Exists(marker) && File.ReadAllText(marker) == runId)
                        Directory.Delete(variantPath, recursive: true);
                    foreach (var temp in Directory.EnumerateFiles(Path.GetDirectoryName(resultPath)!, Path.GetFileName(resultPath) + ".*.tmp"))
                    {
                        // Worker result temporary files carry the owning run identifier.
                        if (Path.GetFileName(temp).Contains(runId)) File.Delete(temp);
                    }
                }
                catch (IOException error) { Console.Error.WriteLine($"Unable to remove incomplete run: {error.Message}"); }
            }
        }
    }
}
