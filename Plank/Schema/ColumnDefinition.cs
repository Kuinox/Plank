using System.Collections.Immutable;
using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record ColumnDefinition
{
    public static ColumnDefinition RequiredGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, children, fieldId: null);

    public static ColumnDefinition RequiredGroup(string name, int fieldId, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, children, fieldId);

    public static ColumnDefinition OptionalGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, children, fieldId: null);

    public static ColumnDefinition OptionalGroup(string name, int fieldId, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, children, fieldId);

    public static ColumnDefinition RequiredLeaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => CreateLeaf(name, ParquetRepetition.Required, physicalType, options, logicalType, pageStrategy, fieldId: null);

    public static ColumnDefinition RequiredLeaf(string name, ParquetPhysicalType physicalType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => CreateLeaf(name, ParquetRepetition.Required, physicalType, options, logicalType, pageStrategy, fieldId);

    public static ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => CreateLeaf(name, ParquetRepetition.Optional, physicalType, options, logicalType, pageStrategy, fieldId: null);

    public static ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => CreateLeaf(name, ParquetRepetition.Optional, physicalType, options, logicalType, pageStrategy, fieldId);

    public static ColumnDefinition Leaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => LeafCore(name, physicalType, options, logicalType, pageStrategy, fieldId: null);

    public static ColumnDefinition Leaf(string name, ParquetPhysicalType physicalType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => LeafCore(name, physicalType, options, logicalType, pageStrategy, fieldId);

    static ColumnDefinition LeafCore(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options, LogicalType? logicalType, IPageStrategy? pageStrategy, int? fieldId)
    {
        var repetition = options?.Repetition is { } configured and not ParquetRepetition.Unspecified
            ? configured
            : ParquetRepetition.Required;
        return CreateLeaf(name, repetition, physicalType, options, logicalType, pageStrategy, fieldId);
    }

    public static ColumnDefinition List(string name, ColumnDefinition element,
        ParquetRepetition repetition = ParquetRepetition.Required)
        => CreateList(name, element, repetition, fieldId: null);

    public static ColumnDefinition List(string name, ColumnDefinition element, int fieldId,
        ParquetRepetition repetition = ParquetRepetition.Required)
        => CreateList(name, element, repetition, fieldId);

    static ColumnDefinition CreateList(string name, ColumnDefinition element, ParquetRepetition repetition,
        int? fieldId)
        => new()
        {
            Name = name,
            Kind = NodeKind.List,
            Repetition = repetition,
            FieldId = fieldId,
            Children = [element]
        };

    public static ColumnDefinition Map(string name, ColumnDefinition key, ColumnDefinition value,
        ParquetRepetition repetition = ParquetRepetition.Required)
        => CreateMap(name, key, value, repetition, fieldId: null);

    public static ColumnDefinition Map(string name, ColumnDefinition key, ColumnDefinition value, int fieldId,
        ParquetRepetition repetition = ParquetRepetition.Required)
        => CreateMap(name, key, value, repetition, fieldId);

    static ColumnDefinition CreateMap(string name, ColumnDefinition key, ColumnDefinition value,
        ParquetRepetition repetition, int? fieldId)
        => new()
        {
            Name = name,
            Kind = NodeKind.Map,
            Repetition = repetition,
            FieldId = fieldId,
            Children = [key, value]
        };

    public required string Name { get; init; }

    public required NodeKind Kind { get; init; }

    public required ParquetRepetition Repetition { get; init; }

    public ParquetPhysicalType? PhysicalType { get; init; }

    public LogicalType? LogicalType { get; init; }

    public int? FieldId { get; init; }

    public ColumnOptions? Options { get; init; }

    public IPageStrategy? PageStrategy { get; init; }

    public ImmutableArray<ColumnDefinition> Children { get; init; } = [];

    static ColumnDefinition Group(string name, ParquetRepetition repetition, ReadOnlySpan<ColumnDefinition> children,
        int? fieldId)
        => new()
        {
            Name = name,
            Kind = NodeKind.Group,
            Repetition = repetition,
            FieldId = fieldId,
            Children = children.Length == 0
                ? []
                : ImmutableArray.Create(children.ToArray())
        };

    static ColumnDefinition CreateLeaf(string name, ParquetRepetition repetition, ParquetPhysicalType physicalType,
        ColumnOptions? options, LogicalType? logicalType, IPageStrategy? pageStrategy, int? fieldId)
    {
        var normalizedOptions = options ?? ColumnOptions.Default;
        if (normalizedOptions.Repetition != repetition)
            normalizedOptions = new ColumnOptions(repetition, normalizedOptions.Encodings, normalizedOptions.TypeLength);
        EncodingCompatibility.Validate(name, physicalType, normalizedOptions);
        ValidateLogicalType(name, physicalType, logicalType);

        return new()
        {
            Name = name,
            Kind = NodeKind.Leaf,
            Repetition = repetition,
            PhysicalType = physicalType,
            LogicalType = logicalType,
            FieldId = fieldId,
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
