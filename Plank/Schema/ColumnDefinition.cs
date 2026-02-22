using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ColumnDefinition
{
    public required string Name { get; init; }

    public required NodeKind Kind { get; init; }

    public required ParquetRepetition Repetition { get; init; }

    public ParquetPhysicalType? PhysicalType { get; init; }

    public ColumnOptions? Options { get; init; }

    public ImmutableArray<ColumnDefinition> Children { get; init; } = [];

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Name);
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Column definition name must not be empty or whitespace.", nameof(Name));
        if (!Enum.IsDefined(Kind))
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Kind must be a defined NodeKind value.");
        if (!Enum.IsDefined(Repetition))
            throw new ArgumentOutOfRangeException(nameof(Repetition), Repetition, "Repetition must be a defined ParquetRepetition value.");

        switch (Kind)
        {
            case NodeKind.Leaf:
                ValidateLeaf();
                break;
            case NodeKind.Group:
            case NodeKind.List:
            case NodeKind.Map:
                ValidateContainer();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Kind must be a defined NodeKind value.");
        }
    }

    void ValidateLeaf()
    {
        if (PhysicalType is null)
            throw new InvalidOperationException($"Leaf node '{Name}' requires a physical type.");
        if (!Enum.IsDefined(PhysicalType.Value))
            throw new ArgumentOutOfRangeException(nameof(PhysicalType), PhysicalType, "PhysicalType must be a defined ParquetPhysicalType value.");
        if (!Children.IsDefaultOrEmpty)
            throw new InvalidOperationException($"Leaf node '{Name}' cannot have child nodes.");

        var options = Options ?? ColumnOptions.Default;
        options.Validate();
    }

    void ValidateContainer()
    {
        if (PhysicalType is not null)
            throw new InvalidOperationException($"Container node '{Name}' cannot define a physical type.");
        if (Options is not null)
            throw new InvalidOperationException($"Container node '{Name}' cannot define leaf column options.");
        if (Children.IsDefaultOrEmpty)
            throw new InvalidOperationException($"Container node '{Name}' must contain at least one child node.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Children.Length; i++)
        {
            var child = Children[i];
            ArgumentNullException.ThrowIfNull(child);
            child.Validate();
            if (!seen.Add(child.Name))
                throw new InvalidOperationException($"Node '{Name}' has duplicate child name '{child.Name}'.");
        }
    }
}
