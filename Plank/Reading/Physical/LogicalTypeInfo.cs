using Plank.Schema;

namespace Plank.Reading.Physical;

/// <summary>
/// Carries a Parquet logical type annotation and its kind-specific fields.
/// </summary>
public readonly record struct LogicalTypeInfo(LogicalTypeKind Kind, int Precision = 0, int Scale = 0,
    byte BitWidth = 0, bool IsSigned = false, TimeUnit Unit = default, bool IsAdjustedToUtc = false)
{
    public sbyte? SpecificationVersion { get; internal init; }

    public EdgeInterpolationAlgorithm? Algorithm { get; internal init; }

    public bool HasCrs { get; internal init; }

    internal int CrsOffset { get; init; }

    internal int CrsLength { get; init; }
}
