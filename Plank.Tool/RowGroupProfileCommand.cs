using Plank.Tool.Querying;
using ConsoleAppFramework;

namespace Plank.Tool;

internal static class RowGroupProfileCommand
{
    /// <summary>Repack compressed pages and benchmark native engine footer inspection.</summary>
    /// <param name="file">Input Parquet file.</param>
    /// <param name="sizes">Comma-separated target row counts per row group.</param>
    /// <param name="engines">Comma-separated engines: duckdb, polars, datafusion, clickhouse, spark.</param>
    /// <param name="output">Result Parquet path; defaults to INPUT.rowgroups-profile.parquet.</param>
    /// <param name="variants">Directory for generated files; defaults to OUTPUT without its extension.</param>
    /// <param name="iterations">Measured inspections per engine and variant.</param>
    /// <param name="warmup">Untimed inspections before measurements.</param>
    /// <param name="threads">Engine thread count.</param>
    /// <param name="workerDirectory">Directory for the automatically managed Python project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int> Run([Argument] string file, string sizes = "10000,100000,1000000",
        string engines = "duckdb,polars,datafusion", string? output = null, string? variants = null,
        int iterations = 50, int warmup = 5, int threads = 1, string? workerDirectory = null,
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
            output = Path.GetFullPath(output ?? Path.ChangeExtension(file, ".rowgroups-profile.parquet"));
            variants = Path.GetFullPath(variants ?? Path.ChangeExtension(output, null));
            if (Path.GetFullPath(file) == output || File.Exists(output) || Directory.Exists(output))
                throw new IOException("Result output already exists or aliases the input.");
            if (Directory.Exists(variants) || File.Exists(variants)) throw new IOException("Variant directory already exists.");
            if (!Directory.Exists(Path.GetDirectoryName(output))) throw new DirectoryNotFoundException("Result parent directory does not exist.");
            resultPath = output;
            variantPath = variants;
            var project = await PythonProject.Ensure(workerDirectory, selected, cancellationToken);
            var path = await project.Run(new { kind = "rowgroups", run_id = runId, file, sizes = targets, engines = selected,
                output, variants, iterations, warmup, threads }, cancellationToken);
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
