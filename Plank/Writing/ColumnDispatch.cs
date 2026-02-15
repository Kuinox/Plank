using Plank.Schema;

namespace Plank.Writing;

static partial class ColumnDispatch
{
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
