using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;

namespace Plank.Benchmarks;

public sealed class EncodingMatrixBenchmarkConfig : ManualConfig
{
    public EncodingMatrixBenchmarkConfig()
    {
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumn(new EncodingSizeMetricColumn("ColumnCompressedBytes", static x => x.ColumnCompressedBytes));
        AddColumn(new EncodingSizeMetricColumn("ColumnUncompressedBytes", static x => x.ColumnUncompressedBytes));
        AddColumn(new EncodingSizeMetricColumn("FileBytes", static x => x.FileBytes));
    }
}
