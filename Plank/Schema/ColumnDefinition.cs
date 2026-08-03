using System.Collections.Immutable;
using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record ColumnDefinition
{
    public static ColumnDefinition RequiredGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, logicalType: null, children, fieldId: null);

    public static ColumnDefinition RequiredGroup(string name, int fieldId, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, logicalType: null, children, fieldId);

    public static ColumnDefinition RequiredGroup(string name, LogicalType logicalType,
        params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, logicalType, children, fieldId: null);

    public static ColumnDefinition OptionalGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, logicalType: null, children, fieldId: null);

    public static ColumnDefinition OptionalGroup(string name, int fieldId, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, logicalType: null, children, fieldId);

    public static ColumnDefinition OptionalGroup(string name, LogicalType logicalType,
        params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, logicalType, children, fieldId: null);

    public static ColumnDefinition RequiredLeaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => CreateLeaf(name, ParquetRepetition.Required, physicalType, options, logicalType, pageStrategy,
            fieldId: null, converter);

    public static ColumnDefinition RequiredLeaf(string name, ParquetPhysicalType physicalType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => CreateLeaf(name, ParquetRepetition.Required, physicalType, options, logicalType, pageStrategy, fieldId,
            converter);

    public static ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => CreateLeaf(name, ParquetRepetition.Optional, physicalType, options, logicalType, pageStrategy,
            fieldId: null, converter);

    public static ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => CreateLeaf(name, ParquetRepetition.Optional, physicalType, options, logicalType, pageStrategy, fieldId,
            converter);

    public static ColumnDefinition Leaf(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => LeafCore(name, physicalType, options, logicalType, pageStrategy, fieldId: null, converter);

    public static ColumnDefinition Leaf(string name, ParquetPhysicalType physicalType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null,
        ParquetValueConverter? converter = null)
        => LeafCore(name, physicalType, options, logicalType, pageStrategy, fieldId, converter);

    static ColumnDefinition LeafCore(string name, ParquetPhysicalType physicalType,
        ColumnOptions? options, LogicalType? logicalType, IPageStrategy? pageStrategy, int? fieldId,
        ParquetValueConverter? converter)
    {
        var repetition = options?.Repetition is { } configured and not ParquetRepetition.Unspecified
            ? configured
            : ParquetRepetition.Required;
        return CreateLeaf(name, repetition, physicalType, options, logicalType, pageStrategy, fieldId, converter);
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

    /// <summary>Gets the custom CLR value converter for this leaf, if one is declared.</summary>
    public ParquetValueConverter? Converter { get; init; }

    public ImmutableArray<ColumnDefinition> Children { get; init; } = [];

    static ColumnDefinition Group(string name, ParquetRepetition repetition, LogicalType? logicalType,
        ReadOnlySpan<ColumnDefinition> children, int? fieldId)
    {
        ValidateGroupLogicalType(name, logicalType, children);
        return new()
        {
            Name = name,
            Kind = NodeKind.Group,
            Repetition = repetition,
            FieldId = fieldId,
            LogicalType = logicalType,
            Children = children.Length == 0
                ? []
                : ImmutableArray.Create(children.ToArray())
        };
    }

    static ColumnDefinition CreateLeaf(string name, ParquetRepetition repetition, ParquetPhysicalType physicalType,
        ColumnOptions? options, LogicalType? logicalType, IPageStrategy? pageStrategy,
        int? fieldId, ParquetValueConverter? converter)
    {
        var normalizedOptions = options ?? ColumnOptions.Default;
        if (normalizedOptions.Repetition != repetition)
            normalizedOptions = new ColumnOptions(repetition, normalizedOptions.Encodings, normalizedOptions.TypeLength,
                normalizedOptions.Compression, normalizedOptions.CompressionLevel, normalizedOptions.BloomFilter);
        EncodingCompatibility.Validate(name, physicalType, normalizedOptions);
        ValidateLogicalType(name, physicalType, normalizedOptions, logicalType);
        ValidateConverter(name, physicalType, normalizedOptions, converter);

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

    static int GetMaximumDecimalPrecision(uint typeLength)
    {
        if (typeLength == 0)
            return 0;

        var bits = (double)typeLength * 8 - 1;
        var precision = Math.Floor(bits * Math.Log10(2));
        return precision >= int.MaxValue ? int.MaxValue : checked((int)precision);
    }
}
