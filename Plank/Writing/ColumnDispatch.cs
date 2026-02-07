using Plank.Schema;

namespace Plank.Writing;

static class ColumnDispatch
{
    internal enum ValueKind
    {
        Unknown = 0,
        Bool,
        NullableBool,
        Int32,
        NullableInt32,
        Int64,
        NullableInt64,
        Float,
        NullableFloat,
        Double,
        NullableDouble,
        String,
        ByteArray,
        DateTime,
        NullableDateTime,
        DateTimeOffset,
        NullableDateTimeOffset,
        DateOnly,
        NullableDateOnly,
        TimeOnly,
        NullableTimeOnly
    }

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

    internal static ValueKind GetValueKind<T>() =>
        typeof(T) switch
        {
            var t when t == typeof(bool) => ValueKind.Bool,
            var t when t == typeof(bool?) => ValueKind.NullableBool,
            var t when t == typeof(int) => ValueKind.Int32,
            var t when t == typeof(int?) => ValueKind.NullableInt32,
            var t when t == typeof(long) => ValueKind.Int64,
            var t when t == typeof(long?) => ValueKind.NullableInt64,
            var t when t == typeof(float) => ValueKind.Float,
            var t when t == typeof(float?) => ValueKind.NullableFloat,
            var t when t == typeof(double) => ValueKind.Double,
            var t when t == typeof(double?) => ValueKind.NullableDouble,
            var t when t == typeof(string) => ValueKind.String,
            var t when t == typeof(byte[]) => ValueKind.ByteArray,
            var t when t == typeof(DateTime) => ValueKind.DateTime,
            var t when t == typeof(DateTime?) => ValueKind.NullableDateTime,
            var t when t == typeof(DateTimeOffset) => ValueKind.DateTimeOffset,
            var t when t == typeof(DateTimeOffset?) => ValueKind.NullableDateTimeOffset,
            var t when t == typeof(DateOnly) => ValueKind.DateOnly,
            var t when t == typeof(DateOnly?) => ValueKind.NullableDateOnly,
            var t when t == typeof(TimeOnly) => ValueKind.TimeOnly,
            var t when t == typeof(TimeOnly?) => ValueKind.NullableTimeOnly,
            _ => ValueKind.Unknown
        };

    internal static DispatchKey GetDispatchKey(ParquetPhysicalType physicalType, ValueKind valueKind)
        => (DispatchKey)(((int)physicalType << 8) | (int)valueKind);
}
