using System.Collections.Immutable;
using Plank.Schema;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests;

static partial class GeneratedSchemaHolder
{
    [GenerateRowApi]
    public static ParquetSchema Schema { get; } = new([
        new PlankColumn("id", ParquetPhysicalType.Int32, ColumnOptions.Default),
        new PlankColumn("flag", ParquetPhysicalType.Boolean, ColumnOptions.Default),
        new PlankColumn("amount", ParquetPhysicalType.Int64, ColumnOptions.Default),
        new PlankColumn("ratio", ParquetPhysicalType.Float, ColumnOptions.Default),
        new PlankColumn("score", ParquetPhysicalType.Double, ColumnOptions.Default),
        new PlankColumn("blob", ParquetPhysicalType.ByteArray, ColumnOptions.Default),
        new PlankColumn("opt_int", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional, ImmutableArray<EncodingKind>.Empty))
    ]);
}
