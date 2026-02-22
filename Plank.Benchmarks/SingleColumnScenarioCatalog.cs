namespace Plank.Benchmarks;

public static class SingleColumnScenarioCatalog
{
    static readonly SingleColumnScenario[] AllScenarios = BuildAllScenarios();
    static readonly SingleColumnScenario[] PlankScenarios = BuildSupportedScenarios(IsSupportedByParquetSpecEncodingRules);
    static readonly SingleColumnScenario[] ParquetSharpScenarios = BuildSupportedScenarios(IsSupportedByParquetSpecEncodingRules);
    static readonly SingleColumnScenario[] ParquetNetScenarios = BuildSupportedScenarios(IsSupportedByParquetNet);

    public static IReadOnlyList<SingleColumnScenario> All
        => AllScenarios;

    public static IReadOnlyList<SingleColumnScenario> Plank
        => PlankScenarios;

    public static IReadOnlyList<SingleColumnScenario> ParquetSharp
        => ParquetSharpScenarios;

    public static IReadOnlyList<SingleColumnScenario> ParquetNet
        => ParquetNetScenarios;

    static SingleColumnScenario[] BuildAllScenarios()
    {
        var scenarios = new List<SingleColumnScenario>();
        foreach (var dataType in EnumerateDataTypes())
            foreach (var encoding in EnumerateEncodings())
                scenarios.Add(new SingleColumnScenario(dataType, encoding));
        return [.. scenarios];
    }

    static SingleColumnScenario[] BuildSupportedScenarios(Func<string, string, bool> isSupported)
    {
        var scenarios = new List<SingleColumnScenario>();
        foreach (var scenario in AllScenarios)
            if (isSupported(scenario.DataType, scenario.EncodingName))
                scenarios.Add(scenario);
        return [.. scenarios];
    }

    static bool IsSupportedByParquetSpecEncodingRules(string dataType, string encoding)
        => encoding switch
        {
            "plain" => IsKnownDataType(dataType),
            "dictionary" => dataType is not "bool" && IsKnownDataType(dataType),
            "delta_binary_packed" => dataType is "int32" or "int64",
            "delta_length_byte_array" => dataType == "string",
            "delta_byte_array" => dataType == "string",
            "byte_stream_split" => dataType is "float" or "double",
            _ => false
        };

    static bool IsSupportedByParquetNet(string dataType, string encoding)
        => encoding switch
        {
            "plain" => IsKnownDataType(dataType),
            "dictionary" => dataType is not "bool" && IsKnownDataType(dataType),
            "delta_binary_packed" => dataType is "int32" or "int64",
            _ => false
        };

    static bool IsKnownDataType(string dataType)
        => dataType is "bool" or "int32" or "int64" or "float" or "double" or "string";

    static IEnumerable<string> EnumerateDataTypes()
    {
        yield return "bool";
        yield return "int32";
        yield return "int64";
        yield return "float";
        yield return "double";
        yield return "string";
    }

    static IEnumerable<string> EnumerateEncodings()
    {
        yield return "plain";
        yield return "dictionary";
        yield return "delta_binary_packed";
        yield return "delta_length_byte_array";
        yield return "delta_byte_array";
        yield return "byte_stream_split";
    }
}
