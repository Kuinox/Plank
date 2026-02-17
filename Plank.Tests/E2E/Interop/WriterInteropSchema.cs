using Plank.Schema;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests;

static class WriterInteropSchema
{
    public const string Int32ColumnName = "I32";
    public const string Int64ColumnName = "I64";
    public const string DoubleColumnName = "F64";
    public const string BinaryColumnName = "BIN";

    public static readonly ParquetSchema Schema = new([
        new PlankColumn(Int32ColumnName, ParquetPhysicalType.Int32, ColumnOptions.Default),
        new PlankColumn(Int64ColumnName, ParquetPhysicalType.Int64, ColumnOptions.Default),
        new PlankColumn(DoubleColumnName, ParquetPhysicalType.Double, ColumnOptions.Default),
        new PlankColumn(BinaryColumnName, ParquetPhysicalType.ByteArray, ColumnOptions.Default)
    ]);
}
