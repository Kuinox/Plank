using Plank.Schema;

namespace Plank.Tests.E2E.Interop;

static class WriterInteropSchema
{
    public const string Int32ColumnName = "I32";
    public const string Int64ColumnName = "I64";
    public const string DoubleColumnName = "F64";
    public const string BinaryColumnName = "BIN";

    public static readonly ParquetSchema Schema = new([
        Plank.Schema.ColumnDefinition.Leaf(Int32ColumnName, ParquetPhysicalType.Int32, ColumnOptions.Default),
        Plank.Schema.ColumnDefinition.Leaf(Int64ColumnName, ParquetPhysicalType.Int64, ColumnOptions.Default),
        Plank.Schema.ColumnDefinition.Leaf(DoubleColumnName, ParquetPhysicalType.Double, ColumnOptions.Default),
        Plank.Schema.ColumnDefinition.Leaf(BinaryColumnName, ParquetPhysicalType.ByteArray, ColumnOptions.Default)
    ]);
}
