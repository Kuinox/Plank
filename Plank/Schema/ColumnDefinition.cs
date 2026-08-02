using System.Collections.Immutable;
using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record ColumnDefinition
{
    public static ColumnDefinition RequiredGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, null, children);

    public static ColumnDefinition RequiredGroup(string name, LogicalType logicalType,
        params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, logicalType, children);

    public static ColumnDefinition OptionalGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, null, children);

    public static ColumnDefinition OptionalGroup(string name, LogicalType logicalType,
        params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, logicalType, children);

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

    static ColumnDefinition Group(string name, ParquetRepetition repetition, LogicalType? logicalType,
        ReadOnlySpan<ColumnDefinition> children)
    {
        ValidateGroupLogicalType(name, logicalType, children);
        return new()
        {
            Name = name,
            Kind = NodeKind.Group,
            Repetition = repetition,
            LogicalType = logicalType,
            Children = children.Length == 0
                ? []
                : ImmutableArray.Create(children.ToArray())
        };
    }

    static ColumnDefinition CreateLeaf(string name, ParquetRepetition repetition, ParquetPhysicalType physicalType,
        ColumnOptions? options, LogicalType? logicalType, IPageStrategy? pageStrategy)
    {
        var normalizedOptions = options ?? ColumnOptions.Default;
        if (normalizedOptions.Repetition != repetition)
            normalizedOptions = new ColumnOptions(repetition, normalizedOptions.Encodings, normalizedOptions.TypeLength);
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

        if (logicalType is LogicalType.Enum or LogicalType.Bson or LogicalType.Geometry or LogicalType.Geography &&
            physicalType != ParquetPhysicalType.ByteArray)
            throw new ArgumentException(
                $"Logical type '{logicalType.GetType().Name}' requires physical type '{ParquetPhysicalType.ByteArray}' for column '{name}'.",
                nameof(physicalType));

        if (logicalType is LogicalType.Float16 &&
            (physicalType != ParquetPhysicalType.FixedLenByteArray || options.TypeLength != 2))
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Float16)}' requires a 2-byte '{ParquetPhysicalType.FixedLenByteArray}' physical type for column '{name}'.",
                nameof(physicalType));

        if (logicalType is LogicalType.Interval &&
            (physicalType != ParquetPhysicalType.FixedLenByteArray || options.TypeLength != 12))
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Interval)}' requires a 12-byte '{ParquetPhysicalType.FixedLenByteArray}' physical type for column '{name}'.",
                nameof(physicalType));

        if (logicalType is LogicalType.Variant)
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Variant)}' requires a group for column '{name}'.",
                nameof(logicalType));

        if (logicalType is LogicalType.Unknown && options.Repetition != ParquetRepetition.Optional)
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Unknown)}' requires an optional column because all values must be null for column '{name}'.",
                nameof(logicalType));
    }

    internal static void ValidateGroupLogicalType(string name, LogicalType? logicalType,
        ReadOnlySpan<ColumnDefinition> children)
    {
        if (logicalType is null)
            return;
        if (logicalType is not LogicalType.Variant)
            throw new ArgumentException(
                $"Logical type '{logicalType.GetType().Name}' cannot annotate group '{name}'.",
                nameof(logicalType));

        ColumnDefinition? metadata = null;
        ColumnDefinition? value = null;
        for (var i = 0; i < children.Length; i++)
            if (children[i].Name == "metadata")
                metadata = children[i];
            else if (children[i].Name == "value")
                value = children[i];

        if (metadata is not
            {
                Kind: NodeKind.Leaf,
                Repetition: ParquetRepetition.Required,
                PhysicalType: ParquetPhysicalType.ByteArray
            })
            throw new ArgumentException(
                $"Variant group '{name}' requires a BYTE_ARRAY field named 'metadata'.",
                nameof(children));
        if (value is not
            {
                Kind: NodeKind.Leaf,
                Repetition: ParquetRepetition.Required or ParquetRepetition.Optional,
                PhysicalType: ParquetPhysicalType.ByteArray
            })
            throw new ArgumentException(
                $"Variant group '{name}' requires a BYTE_ARRAY field named 'value'.",
                nameof(children));
    }
}
