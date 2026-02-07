namespace Plank.Schema;

static class ParquetTypeMap
{
    public static ParquetPhysicalType GetPhysicalType<T>()
        => TypeCache<T>.PhysicalType;

    static ParquetPhysicalType MapPhysicalType(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        return unwrapped switch
        {
            var t when t == typeof(bool) => ParquetPhysicalType.Boolean,
            var t when t == typeof(int) => ParquetPhysicalType.Int32,
            var t when t == typeof(DateOnly) => ParquetPhysicalType.Int32,
            var t when t == typeof(long) => ParquetPhysicalType.Int64,
            var t when t == typeof(DateTime) => ParquetPhysicalType.Int64,
            var t when t == typeof(DateTimeOffset) => ParquetPhysicalType.Int64,
            var t when t == typeof(TimeOnly) => ParquetPhysicalType.Int64,
            var t when t == typeof(float) => ParquetPhysicalType.Float,
            var t when t == typeof(double) => ParquetPhysicalType.Double,
            var t when t == typeof(byte[]) => ParquetPhysicalType.ByteArray,
            var t when t == typeof(string) => ParquetPhysicalType.ByteArray,
            _ => throw new NotSupportedException($"Unsupported CLR type: {type}.")
        };
    }

    static class TypeCache<T>
    {
        public static readonly ParquetPhysicalType PhysicalType = MapPhysicalType(typeof(T));
    }
}
