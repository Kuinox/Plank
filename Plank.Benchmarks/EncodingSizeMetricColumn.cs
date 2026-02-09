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
        var library = benchmarkCase.Descriptor.WorkloadMethod.Name switch
        {
            nameof(EncodingMatrixBdnBenchmark.WritePlankAsync) => "plank",
            nameof(EncodingMatrixBdnBenchmark.WriteParquetSharp) => "parquetsharp",
            nameof(EncodingMatrixBdnBenchmark.WriteParquetNetAsync) => "parquet.net",
            _ => string.Empty
        };
        if (library.Length == 0)
            return "?";
        if (benchmarkCase.Parameters[nameof(EncodingMatrixBdnBenchmark.DataType)] is not string dataType)
            return "?";
        if (benchmarkCase.Parameters[nameof(EncodingMatrixBdnBenchmark.EncodingName)] is not string encoding)
            return "?";
        if (benchmarkCase.Parameters[nameof(EncodingMatrixBdnBenchmark.Rows)] is not int rows)
            return "?";
        return EncodingBenchmarkMetrics.TryGet(library, dataType, encoding, rows, out var snapshot)
            ? _selector(snapshot).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";
    }

    public override string ToString()
        => ColumnName;
}
