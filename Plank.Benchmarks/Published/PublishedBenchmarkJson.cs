using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plank.Benchmarks.Published;

public static class PublishedBenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(PublishedBenchmarkReport report)
        => JsonSerializer.Serialize(report, Options) + Environment.NewLine;

    public static PublishedBenchmarkReport Deserialize(string json)
        => JsonSerializer.Deserialize<PublishedBenchmarkReport>(json, Options)
           ?? throw new InvalidDataException("The benchmark snapshot is empty.");
}
