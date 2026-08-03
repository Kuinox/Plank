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
                normalizedOptions.Compression, normalizedOptions.CompressionLevel, normalizedOptions.BloomFilter);
        EncodingCompatibility.Validate(name, physicalType, normalizedOptions);
        ValidateLogicalType(name, physicalType, logicalType);

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

    internal static void ValidateLogicalType(string name, ParquetPhysicalType physicalType, LogicalType? logicalType)
    {
        if (logicalType is LogicalType.Date && physicalType != ParquetPhysicalType.Int32)
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Date)}' requires physical type '{ParquetPhysicalType.Int32}' for column '{name}'.",
                nameof(physicalType));
    }
}
