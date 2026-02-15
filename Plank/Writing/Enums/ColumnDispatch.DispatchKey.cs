using Plank.Schema;

namespace Plank.Writing;

static partial class ColumnDispatch
{
    internal enum DispatchKey
    {
        Unknown = 0,
        BooleanBool = (ParquetPhysicalType.Boolean << 8) | ValueKind.Bool,
        BooleanNullableBool = (ParquetPhysicalType.Boolean << 8) | ValueKind.NullableBool,
        Int32Int32 = (ParquetPhysicalType.Int32 << 8) | ValueKind.Int32,
        Int32NullableInt32 = (ParquetPhysicalType.Int32 << 8) | ValueKind.NullableInt32,
        Int32DateOnly = (ParquetPhysicalType.Int32 << 8) | ValueKind.DateOnly,
        Int32NullableDateOnly = (ParquetPhysicalType.Int32 << 8) | ValueKind.NullableDateOnly,
        Int64Int64 = (ParquetPhysicalType.Int64 << 8) | ValueKind.Int64,
        Int64NullableInt64 = (ParquetPhysicalType.Int64 << 8) | ValueKind.NullableInt64,
        Int64DateTime = (ParquetPhysicalType.Int64 << 8) | ValueKind.DateTime,
        Int64NullableDateTime = (ParquetPhysicalType.Int64 << 8) | ValueKind.NullableDateTime,
        Int64DateTimeOffset = (ParquetPhysicalType.Int64 << 8) | ValueKind.DateTimeOffset,
        Int64NullableDateTimeOffset = (ParquetPhysicalType.Int64 << 8) | ValueKind.NullableDateTimeOffset,
        Int64TimeOnly = (ParquetPhysicalType.Int64 << 8) | ValueKind.TimeOnly,
        Int64NullableTimeOnly = (ParquetPhysicalType.Int64 << 8) | ValueKind.NullableTimeOnly,
        ByteArrayString = (ParquetPhysicalType.ByteArray << 8) | ValueKind.String,
        ByteArrayByteArray = (ParquetPhysicalType.ByteArray << 8) | ValueKind.ByteArray,
        FloatFloat = (ParquetPhysicalType.Float << 8) | ValueKind.Float,
        FloatNullableFloat = (ParquetPhysicalType.Float << 8) | ValueKind.NullableFloat,
        DoubleDouble = (ParquetPhysicalType.Double << 8) | ValueKind.Double,
        DoubleNullableDouble = (ParquetPhysicalType.Double << 8) | ValueKind.NullableDouble
    }
}
