using ConsoleAppFramework;
using Plank.Tool.Querying;
using System.Text.Json;
using Plank.Schema;

namespace Plank.Tool;

internal static class QueryProfileCommand
{
    /// <summary>Benchmark SQL against a Parquet file using automatically installed engines.</summary>
    /// <param name="file">Input Parquet file, registered as data.</param>
    /// <param name="query">SQL to execute; every result row is consumed.</param>
    /// <param name="engines">Comma-separated duckdb, polars, datafusion, clickhouse, spark.</param>
    /// <param name="output">Result path, default INPUT.query-profile.parquet.</param>
    /// <param name="iterations">Measured repetitions.</param>
    /// <param name="warmup">Untimed repetitions.</param>
    /// <param name="threads">Engine thread count.</param>
    /// <param name="workerDirectory">Managed Python project directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int> Run([Argument] string file, string query, string engines = "duckdb,polars,datafusion",
        string? output = null, int iterations = 5, int warmup = 2, int threads = 1,
        string? workerDirectory = null, CancellationToken cancellationToken = default)
    {
        try
        {
            file = CommandFiles.Input(file); output = CommandFiles.Output(output, file, ".query-profile.parquet");
            if (string.IsNullOrWhiteSpace(query) || iterations <= 0 || warmup < 0 || threads <= 0)
                throw new ArgumentException("Query is required; iterations/threads must be positive and warmup non-negative.");
            var selected = PythonProject.Engines(engines);
            var project = await PythonProject.Ensure(workerDirectory, selected, cancellationToken);
            var json = await project.Run(new { kind = "query", file, query, engines = selected, iterations, warmup, threads }, cancellationToken);
            var records = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;
            var rows = records.Select(r => r.ToDictionary(x => x.Key, x => x.Value.ValueKind switch
            {
                JsonValueKind.String => (object?)x.Value.GetString(), JsonValueKind.Number => x.Value.TryGetInt64(out var i) ? i : x.Value.GetDouble(), _ => null
            })).ToArray();
            foreach (var row in rows) row["file"] = file;
            ProfileOutput.Write(output, rows, [new("file", ParquetPhysicalType.ByteArray), new("engine", ParquetPhysicalType.ByteArray),
                new("engine_version", ParquetPhysicalType.ByteArray), new("query", ParquetPhysicalType.ByteArray),
                new("time_ns", ParquetPhysicalType.Double), new("median_time_ns", ParquetPhysicalType.Double),
                new("iterations", ParquetPhysicalType.Int64), new("threads", ParquetPhysicalType.Int64), new("result_rows", ParquetPhysicalType.Int64)],
                "query", "1", "Query planning, execution and full result consumption; startup, registration, warmups and validation excluded. Warm filesystem cache. Repeatability fingerprints canonicalize floating results to 12 significant digits.");
            Console.WriteLine(output); return 0;
        }
        catch (Exception error) { return CommandFiles.Error(error); }
    }
}
