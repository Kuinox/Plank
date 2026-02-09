using System.Collections.Immutable;
using Plank.Schema;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests;

static partial class GeneratedHintedSchemaHolder
{
    [GenerateRowApi]
    [RowApiTypeHint("trip_date", typeof(DateOnly))]
    [RowApiTypeHint("event_time", typeof(DateTime))]
    [RowApiTypeHint("event_time_opt", typeof(DateTime?))]
    [RowApiTypeHint("event_offset", typeof(DateTimeOffset))]
    [RowApiTypeHint("event_clock", typeof(TimeOnly))]
    [RowApiTypeHint("tag", typeof(string))]
    [RowApiTypeHint("tag_opt", typeof(string))]
    public static ParquetSchema Schema { get; } = new([
        new PlankColumn("trip_date", ParquetPhysicalType.Int32, ColumnOptions.Default),
        new PlankColumn("event_time", ParquetPhysicalType.Int64, ColumnOptions.Default),
        new PlankColumn("event_time_opt", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Optional, ImmutableArray<EncodingKind>.Empty)),
        new PlankColumn("event_offset", ParquetPhysicalType.Int64, ColumnOptions.Default),
        new PlankColumn("event_clock", ParquetPhysicalType.Int64, ColumnOptions.Default),
        new PlankColumn("tag", ParquetPhysicalType.ByteArray, ColumnOptions.Default),
        new PlankColumn("tag_opt", ParquetPhysicalType.ByteArray, new ColumnOptions(ParquetRepetition.Optional, ImmutableArray<EncodingKind>.Empty))
    ]);
}
