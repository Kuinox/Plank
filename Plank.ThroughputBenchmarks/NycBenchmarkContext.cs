namespace Plank.ThroughputBenchmarks;

public static class NycBenchmarkContext
{
    const string DataDirectoryKey = "PLANK_BENCH_DATA_DIR";
    const string FileCountKey = "PLANK_BENCH_FILE_COUNT";

    public static BenchmarkDataContext? Current { get; set; }

    public static void SetConfiguration(string dataDirectory, int fileCount)
    {
        Environment.SetEnvironmentVariable(DataDirectoryKey, dataDirectory);
        Environment.SetEnvironmentVariable(FileCountKey, fileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static bool TryGetConfiguration(out string? dataDirectory, out int fileCount)
    {
        dataDirectory = Environment.GetEnvironmentVariable(DataDirectoryKey);
        var fileCountValue = Environment.GetEnvironmentVariable(FileCountKey);
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            fileCount = 0;
            return false;
        }

        if (!int.TryParse(fileCountValue, out fileCount) || fileCount <= 0)
        {
            fileCount = 0;
            return false;
        }

        return true;
    }
}
