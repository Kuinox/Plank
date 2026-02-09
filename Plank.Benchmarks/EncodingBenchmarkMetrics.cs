using System.Globalization;
using System.Text;

namespace Plank.Benchmarks;

public static class EncodingBenchmarkMetrics
{
    static readonly string RootDirectory = Path.Combine(Path.GetTempPath(), "plank-bdn-size-metrics");

    public static void Record(string library, string dataType, string encoding, int rows, EncodingBenchmarkSizeSnapshot snapshot)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = GetPath(library, dataType, encoding, rows);
        var content = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.ColumnCompressedBytes}|{snapshot.ColumnUncompressedBytes}|{snapshot.FileBytes}");
        File.WriteAllText(path, content, Encoding.ASCII);
    }

    public static bool TryGet(string library, string dataType, string encoding, int rows, out EncodingBenchmarkSizeSnapshot snapshot)
    {
        var path = GetPath(library, dataType, encoding, rows);
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

    static string GetPath(string library, string dataType, string encoding, int rows)
    {
        var raw = $"{library}__{dataType}__{encoding}__rows_{rows}";
        var safe = raw.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return Path.Combine(RootDirectory, $"{safe}.txt");
    }
}
