namespace Plank.Schema;

internal static class ParquetTypeMap
{
    public static bool IsNullable(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return Nullable.GetUnderlyingType(type) is not null;
    }

    public static ParquetPhysicalType GetPhysicalType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        return unwrapped switch
        {
            var t when t == typeof(bool) => ParquetPhysicalType.Boolean,
            var t when t == typeof(int) => ParquetPhysicalType.Int32,
            var t when t == typeof(long) => ParquetPhysicalType.Int64,
            var t when t == typeof(float) => ParquetPhysicalType.Float,
            var t when t == typeof(double) => ParquetPhysicalType.Double,
            var t when t == typeof(byte[]) => ParquetPhysicalType.ByteArray,
            var t when t == typeof(string) => ParquetPhysicalType.ByteArray,
            _ => throw new NotSupportedException($"Unsupported CLR type: {type}.")
        };
    }
}
