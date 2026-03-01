using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ColumnDefinition
{
    public required string Name { get; init; }

    public required NodeKind Kind { get; init; }

    public required ParquetRepetition Repetition { get; init; }

    public ParquetPhysicalType? PhysicalType { get; init; }

    public LogicalType? LogicalType { get; init; }

    public ColumnOptions? Options { get; init; }

    public ImmutableArray<ColumnDefinition> Children { get; init; } = [];

}
