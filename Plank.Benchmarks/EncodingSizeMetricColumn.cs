using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Parameters;
using System.Globalization;

namespace Plank.Benchmarks;

public sealed class EncodingSizeMetricColumn : IColumn
{
    readonly string _id;
    readonly Func<EncodingBenchmarkSizeSnapshot, long> _selector;

    public EncodingSizeMetricColumn(string id, Func<EncodingBenchmarkSizeSnapshot, long> selector)
    {
        _id = id;
        _selector = selector;
    }

    public string Id
        => _id;

    public string ColumnName
        => _id;

    public bool AlwaysShow
        => true;

    public ColumnCategory Category
        => ColumnCategory.Custom;

    public int PriorityInCategory
        => 0;

    public bool IsNumeric
        => true;

    public UnitType UnitType
        => UnitType.Size;

    public string Legend
        => $"{_id} from parquet row-group column chunk metadata.";

    public bool IsAvailable(Summary summary)
        => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
        => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var library = benchmarkCase.Descriptor.WorkloadMethod.Name switch
        {
            nameof(EncodingMatrixBdnBenchmark.WritePlankAsync) => "plank",
            nameof(EncodingMatrixBdnBenchmark.WriteParquetSharp) => "parquetsharp",
            nameof(EncodingMatrixBdnBenchmark.WriteParquetNetAsync) => "parquet.net",
            _ => string.Empty
        };
        if (library.Length == 0)
            return "?";
        if (!TryGetScenario(benchmarkCase.Parameters, out var scenario))
            return "?";
        if (!TryGetRows(benchmarkCase.Parameters, out var rows))
            return "?";
        return EncodingBenchmarkMetrics.TryGet(library, scenario.DataType, scenario.EncodingName, rows, out var snapshot)
            ? _selector(snapshot).ToString(CultureInfo.InvariantCulture)
            : "?";
    }

    public override string ToString()
        => ColumnName;

    static bool TryGetScenario(ParameterInstances parameters, out SingleColumnScenario scenario)
    {
        foreach (var item in parameters.Items)
        {
            if (item.Value is SingleColumnScenario typedScenario)
            {
                scenario = typedScenario;
                return true;
            }

            if (item.Value is string scenarioText && SingleColumnScenario.TryParse(scenarioText, out var parsedScenario))
            {
                scenario = parsedScenario;
                return true;
            }
        }

        scenario = default;
        return false;
    }

    static bool TryGetRows(ParameterInstances parameters, out int rows)
    {
        foreach (var item in parameters.Items)
        {
            if (!string.Equals(item.Name, nameof(EncodingMatrixBdnBenchmark.Rows), StringComparison.Ordinal))
                continue;
            if (item.Value is int typedRows)
            {
                rows = typedRows;
                return true;
            }

            if (item.Value is string rowText
                && int.TryParse(rowText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRows))
            {
                rows = parsedRows;
                return true;
            }
        }

        rows = 0;
        return false;
    }
}
