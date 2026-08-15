namespace Plank.Schema;

public abstract record LogicalType
{
    private protected LogicalType() { }

    public sealed record Int : LogicalType
    {
        public Int(byte bitWidth, bool isSigned)
        {
            if (DescribeError(bitWidth) is { } error)
                throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, error);

            BitWidth = bitWidth;
            IsSigned = isSigned;
        }

        /// <summary>Returns why this bit width is invalid, or <see langword="null"/> when it is valid.</summary>
        /// <remarks>
        /// The reader reads the bit width out of a file footer, so a bad one is a corrupt file rather than a caller
        /// mistake. Shared so the two cannot drift.
        /// </remarks>
        internal static string? DescribeError(byte bitWidth)
            => bitWidth is not 8 and not 16 and not 32 and not 64
                ? "Integer logical type bit width must be 8, 16, 32, or 64."
                : null;

        public byte BitWidth { get; }

        public bool IsSigned { get; }
    }

    public sealed record Date : LogicalType;

    public sealed record Time(TimeUnit Unit, bool IsAdjustedToUtc) : LogicalType;

    public sealed record Timestamp(TimeUnit Unit, bool IsAdjustedToUtc) : LogicalType;

    public sealed record String : LogicalType;

    public sealed record Json : LogicalType;

    public sealed record Bson : LogicalType;

    public sealed record Enum : LogicalType;

    public sealed record Uuid : LogicalType;

    public sealed record Float16 : LogicalType;

    public sealed record Interval : LogicalType;

    public sealed record Unknown : LogicalType;

    public sealed record Variant(sbyte? SpecificationVersion = null) : LogicalType;

    public sealed record Geometry(string? Crs = null) : LogicalType;

    public sealed record Geography(string? Crs = null,
        EdgeInterpolationAlgorithm? Algorithm = null) : LogicalType;

    public sealed record Decimal : LogicalType
    {
        public Decimal(int Precision, int Scale)
        {
            if (Precision <= 0)
                throw new ArgumentOutOfRangeException(nameof(Precision), Precision, DescribeError(Precision, Scale));
            if (Scale < 0 || Scale > Precision)
                throw new ArgumentOutOfRangeException(nameof(Scale), Scale, DescribeError(Precision, Scale));

            this.Precision = Precision;
            this.Scale = Scale;
        }

        /// <summary>
        /// Returns why this precision and scale pair is invalid, or <see langword="null"/> when it is valid.
        /// </summary>
        /// <remarks>
        /// The reader reads both out of a file footer and must reject a bad pair as a corrupt file rather than let
        /// this constructor raise <see cref="ArgumentOutOfRangeException"/> at it. Shared so the two cannot drift.
        /// </remarks>
        internal static string? DescribeError(int precision, int scale)
            => precision <= 0 ? "Decimal precision must be positive."
            : scale < 0 || scale > precision ? "Decimal scale must be non-negative and no greater than precision."
            : null;

        public int Precision { get; init; }

        public int Scale { get; init; }
    }
}
