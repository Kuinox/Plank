namespace Plank;

internal static class ParquetTypeMap
{
    public static bool IsNullable(Type type)
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        return Nullable.GetUnderlyingType(type) is not null;
    }

    public static ParquetPhysicalType GetPhysicalType(Type type)
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        if (unwrapped == typeof(bool))
        {
            return ParquetPhysicalType.Boolean;
        }

        if (unwrapped == typeof(int))
        {
            return ParquetPhysicalType.Int32;
        }

        if (unwrapped == typeof(long))
        {
            return ParquetPhysicalType.Int64;
        }

        if (unwrapped == typeof(float))
        {
            return ParquetPhysicalType.Float;
        }

        if (unwrapped == typeof(double))
        {
            return ParquetPhysicalType.Double;
        }

        if (unwrapped == typeof(byte[]))
        {
            return ParquetPhysicalType.ByteArray;
        }

        if (unwrapped == typeof(string))
        {
            return ParquetPhysicalType.ByteArray;
        }

        throw new NotSupportedException($"Unsupported CLR type: {type}.");
    }
}
