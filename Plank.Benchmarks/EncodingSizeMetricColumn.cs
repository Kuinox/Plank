using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

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
        var caseObject = benchmarkCase.Parameters[nameof(EncodingMatrixBdnBenchmark.Case)];
        if (caseObject is not EncodingBenchmarkCase benchmarkCaseKey)
            return "?";
        if (benchmarkCase.Parameters[nameof(EncodingMatrixBdnBenchmark.Rows)] is not int rows)
            return "?";
        return EncodingBenchmarkMetrics.TryGet(benchmarkCaseKey, rows, out var snapshot)
            ? _selector(snapshot).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";
    }

    public override string ToString()
        => ColumnName;
}
