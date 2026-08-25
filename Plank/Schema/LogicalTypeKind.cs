namespace Plank.Schema;

public enum LogicalTypeKind
{
    None = 0,
    String = 1,
    Json = 2,
    Uuid = 3,
    Date = 4,
    Time = 5,
    Timestamp = 6,
    Integer = 7,
    Decimal = 8,
    Enum = 9,
    Bson = 10,
    Float16 = 11,
    Interval = 12,
    Geography = 13,
    Geometry = 14,
    Variant = 15,
    Unknown = 16
}
