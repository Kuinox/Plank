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
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => CreateLeaf(name, ParquetRepetition.Required, physicalType, options, logicalType, pageStrategy, converter);

    public static ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => CreateLeaf(name, ParquetRepetition.Optional, physicalType, options, logicalType, pageStrategy, converter);

    public static ColumnDefinition Leaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
    {
        var repetition = options?.Repetition is { } configured and not ParquetRepetition.Unspecified
            ? configured
            : ParquetRepetition.Required;
        return CreateLeaf(name, repetition, physicalType, options, logicalType, pageStrategy, converter);
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

    /// <summary>Gets the custom CLR value converter for this leaf, if one is declared.</summary>
    public ParquetValueConverter? Converter { get; init; }

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
        ColumnOptions? options, LogicalType? logicalType, IPageStrategy? pageStrategy,
        ParquetValueConverter? converter)
    {
        var normalizedOptions = options ?? ColumnOptions.Default;
        if (normalizedOptions.Repetition != repetition)
            normalizedOptions = new ColumnOptions(repetition, normalizedOptions.Encodings, normalizedOptions.TypeLength);
        EncodingCompatibility.Validate(name, physicalType, normalizedOptions);
        ValidateLogicalType(name, physicalType, logicalType);
        ValidateConverter(name, physicalType, normalizedOptions, converter);

        return new()
        {
            Name = name,
            Kind = NodeKind.Leaf,
            Repetition = repetition,
            PhysicalType = physicalType,
            LogicalType = logicalType,
            Options = normalizedOptions,
            PageStrategy = pageStrategy,
            Converter = converter,
            Children = []
        };
    }

    internal static void ValidateConverter(string name, ParquetPhysicalType physicalType, ColumnOptions options,
        ParquetValueConverter? converter)
    {
        if (converter is null)
            return;
        if (options.Repetition == ParquetRepetition.Repeated)
            throw new ArgumentException(
                $"Custom converters do not support repeated column '{name}'.", nameof(converter));
        if (Nullable.GetUnderlyingType(converter.PhysicalType) is not null)
            throw new ArgumentException(
                $"Converter physical type '{converter.PhysicalType}' must be non-nullable for column '{name}'.",
                nameof(converter));

        var resolution = ParquetTypeMap.ResolvePhysicalType(converter.PhysicalType);
        if (!resolution.IsSuccess)
            throw new ArgumentException(
                $"Converter for column '{name}' uses unsupported physical CLR type '{converter.PhysicalType}'.",
                nameof(converter));
        if (resolution.PhysicalType != physicalType)
            throw new ArgumentException(
                $"Converter physical CLR type '{converter.PhysicalType}' maps to '{resolution.PhysicalType}', " +
                $"not column physical type '{physicalType}' for column '{name}'.", nameof(converter));
        if (physicalType == ParquetPhysicalType.FixedLenByteArray &&
            converter.PhysicalType == typeof(Guid) && options.TypeLength != 16)
            throw new ArgumentException(
                $"Converter physical CLR type '{typeof(Guid)}' requires a 16-byte fixed-length column '{name}'.",
                nameof(converter));
    }

    internal static void ValidateLogicalType(string name, ParquetPhysicalType physicalType, LogicalType? logicalType)
    {
        if (logicalType is LogicalType.Date && physicalType != ParquetPhysicalType.Int32)
            throw new ArgumentException(
                $"Logical type '{nameof(LogicalType.Date)}' requires physical type '{ParquetPhysicalType.Int32}' for column '{name}'.",
                nameof(physicalType));
    }
}
