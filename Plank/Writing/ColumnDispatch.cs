using Plank.Schema;

namespace Plank.Writing;

static class ColumnDispatch
{
    internal enum ValueKind
    {
        Unknown = 0,
        Bool,
        Int32,
        NullableInt32,
        Int64,
        Float,
        Double,
        String,
        ByteArray,
        DateTime,
        DateTimeOffset,
        DateOnly,
        TimeOnly
    }

    internal enum DispatchKey
    {
        Unknown = 0,
        BooleanBool = ((int)ParquetPhysicalType.Boolean << 8) | (int)ValueKind.Bool,
        Int32Int32 = ((int)ParquetPhysicalType.Int32 << 8) | (int)ValueKind.Int32,
        Int32NullableInt32 = ((int)ParquetPhysicalType.Int32 << 8) | (int)ValueKind.NullableInt32,
        Int32DateOnly = ((int)ParquetPhysicalType.Int32 << 8) | (int)ValueKind.DateOnly,
        Int64Int64 = ((int)ParquetPhysicalType.Int64 << 8) | (int)ValueKind.Int64,
        Int64DateTime = ((int)ParquetPhysicalType.Int64 << 8) | (int)ValueKind.DateTime,
        Int64DateTimeOffset = ((int)ParquetPhysicalType.Int64 << 8) | (int)ValueKind.DateTimeOffset,
        Int64TimeOnly = ((int)ParquetPhysicalType.Int64 << 8) | (int)ValueKind.TimeOnly,
        ByteArrayString = ((int)ParquetPhysicalType.ByteArray << 8) | (int)ValueKind.String,
        ByteArrayByteArray = ((int)ParquetPhysicalType.ByteArray << 8) | (int)ValueKind.ByteArray,
        FloatFloat = ((int)ParquetPhysicalType.Float << 8) | (int)ValueKind.Float,
        DoubleDouble = ((int)ParquetPhysicalType.Double << 8) | (int)ValueKind.Double
    }

    internal static ValueKind GetValueKind<T>()
    {
        var type = typeof(T);
        return type switch
        {
            var t when t == typeof(bool) => ValueKind.Bool,
            var t when t == typeof(int) => ValueKind.Int32,
            var t when t == typeof(int?) => ValueKind.NullableInt32,
            var t when t == typeof(long) => ValueKind.Int64,
            var t when t == typeof(float) => ValueKind.Float,
            var t when t == typeof(double) => ValueKind.Double,
            var t when t == typeof(string) => ValueKind.String,
            var t when t == typeof(byte[]) => ValueKind.ByteArray,
            var t when t == typeof(DateTime) => ValueKind.DateTime,
            var t when t == typeof(DateTimeOffset) => ValueKind.DateTimeOffset,
            var t when t == typeof(DateOnly) => ValueKind.DateOnly,
            var t when t == typeof(TimeOnly) => ValueKind.TimeOnly,
            _ => ValueKind.Unknown
        };
    }

    internal static DispatchKey GetDispatchKey(ParquetPhysicalType physicalType, ValueKind valueKind)
        => (DispatchKey)(((int)physicalType << 8) | (int)valueKind);
}
