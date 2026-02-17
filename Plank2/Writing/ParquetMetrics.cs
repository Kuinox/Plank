using System.Diagnostics.Metrics;

namespace Plank2.Writing;

static class ParquetMetrics
{
    static readonly Meter Meter = new("Plank2.Writing", "0.1.0");

    internal static readonly Counter<long> PageListAllocations =
        Meter.CreateCounter<long>("plank2.pagelist.allocations");

    internal static readonly Counter<long> BufferWriterSegmentTableAllocations =
        Meter.CreateCounter<long>("plank2.bufferwriter.segment_table_allocations");
}
