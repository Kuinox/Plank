using System.Collections.Immutable;
using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record ColumnDefinition
{
    public static ColumnDefinition RequiredGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, children);

    public static ColumnDefinition OptionalGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, children);

    public static ColumnDefinition RequiredLeaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => CreateLeaf(name, ParquetRepetition.Required, physicalType, options, logicalType, pageStrategy);

    public static ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => CreateLeaf(name, ParquetRepetition.Optional, physicalType, options, logicalType, pageStrategy);

    public static ColumnDefinition Leaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
    {
        var repetition = options?.Repetition is { } configured and not ParquetRepetition.Unspecified
            ? configured
            : ParquetRepetition.Required;
        return CreateLeaf(name, repetition, physicalType, options, logicalType, pageStrategy);
    }

    public static ColumnDefinition List(string name, ColumnDefinition element,
        ParquetRepetition repetition = ParquetRepetition.Required)
        => new()
        {
            Name = name,
            Kind = NodeKind.List,
            Repetition = repetition,
            Children = [element]
        };

    public static ColumnDefinition Map(string name, ColumnDefinition key, ColumnDefinition value,
        ParquetRepetition repetition = ParquetRepetition.Required)
        => new()
        {
            Name = name,
            Kind = NodeKind.Map,
            Repetition = repetition,
            Children = [key, value]
        };

    public required string Name { get; init; }

    public required NodeKind Kind { get; init; }

    public required ParquetRepetition Repetition { get; init; }

    public ParquetPhysicalType? PhysicalType { get; init; }

    public LogicalType? LogicalType { get; init; }

    public ColumnOptions? Options { get; init; }

    public IPageStrategy? PageStrategy { get; init; }

    public ImmutableArray<ColumnDefinition> Children { get; init; } = [];

    static ColumnDefinition Group(string name, ParquetRepetition repetition, ReadOnlySpan<ColumnDefinition> children)
        => new()
        {
            Name = name,
            Kind = NodeKind.Group,
            Repetition = repetition,
            Children = children.Length == 0
                ? []
                : ImmutableArray.Create(children.ToArray())
        };

    static ColumnDefinition CreateLeaf(string name, ParquetRepetition repetition, ParquetPhysicalType physicalType,
        ColumnOptions? options, LogicalType? logicalType, IPageStrategy? pageStrategy)
    {
        var normalizedOptions = options ?? ColumnOptions.Default;
        if (normalizedOptions.Repetition != repetition)
            normalizedOptions = new ColumnOptions(repetition, normalizedOptions.Encodings, normalizedOptions.TypeLength,
                normalizedOptions.BloomFilter);
        EncodingCompatibility.Validate(name, physicalType, normalizedOptions);
        ValidateLogicalType(name, physicalType, normalizedOptions, logicalType);

        return new()
        {
            Name = name,
            Kind = NodeKind.Leaf,
            Repetition = repetition,
            PhysicalType = physicalType,
            LogicalType = logicalType,
            Options = normalizedOptions,
            PageStrategy = pageStrategy,
            Children = []
        };
    }

    internal static void ValidateLogicalType(string name, ParquetPhysicalType physicalType, ColumnOptions options,
        LogicalType? logicalType)
    {
        if (logicalType is LogicalType.Date && physicalType != ParquetPhysicalType.Int32)
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Date)}' requires physical type '{ParquetPhysicalType.Int32}' for column '{name}'.",
                nameof(physicalType));

        if (logicalType is not LogicalType.Decimal decimalType)
            return;
        if (decimalType.Precision <= 0)
            throw new ArgumentException(
                $"Decimal precision must be positive for column '{name}'.", nameof(logicalType));
        if (decimalType.Scale < 0 || decimalType.Scale > decimalType.Precision)
            throw new ArgumentException(
                $"Decimal scale must be non-negative and no greater than precision for column '{name}'.",
                nameof(logicalType));
        if (physicalType is not (ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64 or
            ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray))
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Decimal)}' is not compatible with physical type '{physicalType}' for column '{name}'.",
                nameof(physicalType));
        if (physicalType == ParquetPhysicalType.Int32 && decimalType.Precision > 9)
            throw new ArgumentException(
                $"Decimal precision {decimalType.Precision} exceeds the maximum precision 9 for INT32 column '{name}'.",
                nameof(logicalType));
        if (physicalType == ParquetPhysicalType.Int64 && decimalType.Precision > 18)
            throw new ArgumentException(
                $"Decimal precision {decimalType.Precision} exceeds the maximum precision 18 for INT64 column '{name}'.",
                nameof(logicalType));
        if (physicalType == ParquetPhysicalType.FixedLenByteArray)
        {
            var maximumPrecision = GetMaximumDecimalPrecision(options.TypeLength);
            if (decimalType.Precision > maximumPrecision)
                throw new ArgumentException(
                    $"Decimal precision {decimalType.Precision} exceeds the maximum precision {maximumPrecision} for {options.TypeLength}-byte column '{name}'.",
                    nameof(logicalType));
        }
    }

    static int GetMaximumDecimalPrecision(uint typeLength)
    {
        if (typeLength == 0)
            return 0;

        var bits = (double)typeLength * 8 - 1;
        var precision = Math.Floor(bits * Math.Log10(2));
        return precision >= int.MaxValue ? int.MaxValue : checked((int)precision);
    }
}
