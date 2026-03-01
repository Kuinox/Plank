namespace Plank.Schema;

public abstract record LogicalType
{
    private protected LogicalType() { }

    public sealed record Date : LogicalType;

    public sealed record Time(TimeUnit Unit, bool IsAdjustedToUtc) : LogicalType;

    public sealed record Timestamp(TimeUnit Unit, bool IsAdjustedToUtc) : LogicalType;

    public sealed record String : LogicalType;

    public sealed record Json : LogicalType;

    public sealed record Uuid : LogicalType;

    public sealed record Decimal(int Precision, int Scale) : LogicalType;
}

public enum TimeUnit
{
    Millis = 0,
    Micros = 1,
    Nanos = 2
}
