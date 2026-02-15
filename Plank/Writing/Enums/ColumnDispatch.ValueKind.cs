namespace Plank.Writing;

static partial class ColumnDispatch
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
}
