namespace Plank.Benchmarks;

static class EncodingSupportMatrix
{
    static readonly string[] DataTypes = ["bool", "int32", "int64", "float", "double", "string"];
    static readonly string[] Encodings =
        ["plain", "dictionary", "delta_binary_packed", "delta_length_byte_array", "delta_byte_array", "byte_stream_split"];

    internal static IEnumerable<EncodingBenchmarkCase> SupportedCases
    {
        get
        {
            foreach (var dataType in DataTypes)
                foreach (var encoding in Encodings)
                    if (IsParquetSupported(dataType, encoding))
                        yield return new EncodingBenchmarkCase(dataType, encoding);
        }
    }

    internal static IEnumerable<EncodingBenchmarkCase> ComparisonCases
    {
        get
        {
            foreach (var dataType in DataTypes)
                foreach (var encoding in Encodings)
                    if (IsParquetSupported(dataType, encoding) && IsParquetNetSupported(dataType, encoding))
                        yield return new EncodingBenchmarkCase(dataType, encoding);
        }
    }

    internal static IEnumerable<EncodingBenchmarkCase> GetSelectedCases()
    {
        var selectedCase = Environment.GetEnvironmentVariable("PLANK_ENCODING_CASE");
        if (string.IsNullOrWhiteSpace(selectedCase))
            return SupportedCases;

        var separator = selectedCase.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == selectedCase.Length - 1)
            throw new InvalidOperationException(
                "PLANK_ENCODING_CASE must use '<data-type>/<encoding>', for example 'float/plain'.");

        var dataType = selectedCase[..separator];
        var encoding = selectedCase[(separator + 1)..];
        if (!IsParquetSupported(dataType, encoding))
            throw new InvalidOperationException($"Encoding benchmark case '{selectedCase}' is not supported.");

        return [new EncodingBenchmarkCase(dataType, encoding)];
    }

    internal static bool IsParquetSupported(string dataType, string encoding)
        => encoding switch
        {
            "plain" => true,
            "dictionary" => true,
            "delta_binary_packed" => dataType is "int32" or "int64",
            "delta_length_byte_array" => dataType is "string",
            "delta_byte_array" => dataType is "string",
            "byte_stream_split" => dataType is "int32" or "int64" or "float" or "double",
            _ => false
        };

    internal static bool IsParquetNetSupported(string dataType, string encoding)
        => encoding switch
        {
            "plain" => true,
            "dictionary" => dataType is "string",
            "delta_binary_packed" => dataType is "int32" or "int64",
            _ => false
        };
}
