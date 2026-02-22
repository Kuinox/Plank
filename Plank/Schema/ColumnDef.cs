using System.Collections.Immutable;

namespace Plank.Schema;

public static class ColumnDef
{
    public static ColumnDefinition RequiredGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Required, children);

    public static ColumnDefinition OptionalGroup(string name, params ColumnDefinition[] children)
        => Group(name, ParquetRepetition.Optional, children);

    public static ColumnDefinition RequiredLeaf(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null)
        => Leaf(name, ParquetRepetition.Required, physicalType, options);

    public static ColumnDefinition OptionalLeaf(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null)
        => Leaf(name, ParquetRepetition.Optional, physicalType, options);

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

    static ColumnDefinition Leaf(string name, ParquetRepetition repetition, ParquetPhysicalType physicalType,
        ColumnOptions? options)
        => new()
        {
            Name = name,
            Kind = NodeKind.Leaf,
            Repetition = repetition,
            PhysicalType = physicalType,
            Options = options ?? ColumnOptions.Default,
            Children = []
        };
}
