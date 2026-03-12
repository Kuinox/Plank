namespace Plank.Schema;

public abstract record LogicalType
{
    private protected LogicalType() { }

    public sealed record Int : LogicalType
    {
        public Int(byte bitWidth, bool isSigned)
        {
            if (bitWidth is not 8 and not 16 and not 32 and not 64)
                throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "Integer logical type bit width must be 8, 16, 32, or 64.");

            BitWidth = bitWidth;
            IsSigned = isSigned;
        }

        public byte BitWidth { get; }

        public bool IsSigned { get; }
    }

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
