using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;

namespace Plank.Benchmarks;

public sealed class EncodingMatrixBenchmarkConfig : ManualConfig
{
    public EncodingMatrixBenchmarkConfig()
    {
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumn(new EncodingSizeMetricColumn("ColumnCompressedBytes", static x => x.ColumnCompressedBytes));
        AddColumn(new EncodingSizeMetricColumn("ColumnUncompressedBytes", static x => x.ColumnUncompressedBytes));
        AddColumn(new EncodingSizeMetricColumn("FileBytes", static x => x.FileBytes));
        WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(80));
    }
}
