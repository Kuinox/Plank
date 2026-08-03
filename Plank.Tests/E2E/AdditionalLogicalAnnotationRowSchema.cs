using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
public sealed partial class AdditionalLogicalAnnotationRowSchema
{
    [ParquetColumn(LogicalType = LogicalTypeKind.Enum)]
    public byte[] EnumValue { get; init; } = [];

    [ParquetColumn(LogicalType = LogicalTypeKind.Bson)]
    public byte[] BsonValue { get; init; } = [];

    [ParquetColumn(ParquetPhysicalType.FixedLenByteArray, LogicalType = LogicalTypeKind.Float16)]
    public byte[] Float16Value { get; init; } = [];

    [ParquetColumn(ParquetPhysicalType.FixedLenByteArray, LogicalType = LogicalTypeKind.Interval)]
    public byte[] IntervalValue { get; init; } = [];

    [ParquetColumn(LogicalType = LogicalTypeKind.Geometry)]
    public byte[] GeometryValue { get; init; } = [];

    [ParquetColumn(LogicalType = LogicalTypeKind.Geography)]
    public byte[] GeographyValue { get; init; } = [];

    [ParquetColumn(LogicalType = LogicalTypeKind.Unknown)]
    public byte[]? UnknownValue { get; init; }
}
