using System.Globalization;
using System.Text;

namespace Plank.Benchmarks;

public static class EncodingBenchmarkMetrics
{
    static readonly string RootDirectory = Path.Combine(Path.GetTempPath(), "plank-bdn-size-metrics");

    public static void Record(EncodingBenchmarkCase benchmarkCase, int rows, EncodingBenchmarkSizeSnapshot snapshot)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = GetPath(benchmarkCase, rows);
        var content = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.ColumnCompressedBytes}|{snapshot.ColumnUncompressedBytes}|{snapshot.FileBytes}");
        File.WriteAllText(path, content, Encoding.ASCII);
    }

    public static bool TryGet(EncodingBenchmarkCase benchmarkCase, int rows, out EncodingBenchmarkSizeSnapshot snapshot)
    {
        var path = GetPath(benchmarkCase, rows);
        if (!File.Exists(path))
        {
            snapshot = default;
            return false;
        }

        var content = File.ReadAllText(path, Encoding.ASCII);
        var segments = content.Split('|');
        if (segments.Length != 3
            || !long.TryParse(segments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var compressed)
            || !long.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uncompressed)
            || !long.TryParse(segments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileBytes))
        {
            snapshot = default;
            return false;
        }

        snapshot = new EncodingBenchmarkSizeSnapshot(compressed, uncompressed, fileBytes);
        return true;
    }

    static string GetPath(EncodingBenchmarkCase benchmarkCase, int rows)
    {
        var raw = $"{benchmarkCase.Library}__{benchmarkCase.DataType}__{benchmarkCase.Encoding}__rows_{rows}";
        var safe = raw.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return Path.Combine(RootDirectory, $"{safe}.txt");
    }
}
