namespace Plank.Schema;

static class ParquetTypeMap
{
    public readonly struct PhysicalTypeResolution
    {
        internal PhysicalTypeResolution(ParquetPhysicalType physicalType, string? errorMessage)
        {
            PhysicalType = physicalType;
            ErrorMessage = errorMessage;
        }

        public ParquetPhysicalType PhysicalType { get; }
        public string? ErrorMessage { get; }
        public bool IsSuccess
            => ErrorMessage is null;
    }

    public static PhysicalTypeResolution ResolvePhysicalType<T>()
        => TypeCache<T>.Resolution;

    public static PhysicalTypeResolution ResolvePhysicalType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return CreateResolution(type);
    }

    public static bool TryGetPhysicalType<T>(out ParquetPhysicalType physicalType)
    {
        var resolution = ResolvePhysicalType<T>();
        physicalType = resolution.PhysicalType;
        return resolution.IsSuccess;
    }

    public static ParquetPhysicalType GetPhysicalType<T>()
    {
        var resolution = ResolvePhysicalType<T>();
        if (resolution.IsSuccess)
            return resolution.PhysicalType;
        throw new NotSupportedException(resolution.ErrorMessage);
    }

    static PhysicalTypeResolution CreateResolution(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        if (TryMapPhysicalType(unwrapped, out var physicalType))
            return new PhysicalTypeResolution(physicalType, null);

        return new PhysicalTypeResolution(default, $"Unsupported CLR type: {type}.");
    }

    static bool TryMapPhysicalType(Type type, out ParquetPhysicalType physicalType)
    {
        switch (type)
        {
            case var t when t == typeof(bool):
                physicalType = ParquetPhysicalType.Boolean;
                return true;
            case var byteType when byteType == typeof(byte):
            case var ushortType when ushortType == typeof(ushort):
            case var intType when intType == typeof(int):
            case var uintType when uintType == typeof(uint):
            case var dateOnlyType when dateOnlyType == typeof(DateOnly):
                physicalType = ParquetPhysicalType.Int32;
                return true;
            case var ulongType when ulongType == typeof(ulong):
            case var longType when longType == typeof(long):
            case var dateTimeType when dateTimeType == typeof(DateTime):
            case var dateTimeOffsetType when dateTimeOffsetType == typeof(DateTimeOffset):
            case var timeOnlyType when timeOnlyType == typeof(TimeOnly):
                physicalType = ParquetPhysicalType.Int64;
                return true;
            case var t when t == typeof(float):
                physicalType = ParquetPhysicalType.Float;
                return true;
            case var t when t == typeof(double):
                physicalType = ParquetPhysicalType.Double;
                return true;
            case var t when t == typeof(byte[]):
            case var t2 when t2 == typeof(string):
                physicalType = ParquetPhysicalType.ByteArray;
                return true;
            default:
                physicalType = default;
                return false;
        }
    }

    static class TypeCache<T>
    {
        public static readonly PhysicalTypeResolution Resolution = CreateResolution(typeof(T));
    }
}
