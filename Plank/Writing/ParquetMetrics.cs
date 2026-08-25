using System.Diagnostics.Metrics;

namespace Plank.Writing;

static class ParquetMetrics
{
    static readonly Meter _meter = new("Plank.Writing", "0.1.0");

    internal static readonly Counter<long> PageListAllocations =
        _meter.CreateCounter<long>("plank2.pagelist.allocations");

    internal static readonly Counter<long> BufferWriterSegmentTableAllocations =
        _meter.CreateCounter<long>("plank2.bufferwriter.segment_table_allocations");
}
