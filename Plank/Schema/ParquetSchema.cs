using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ParquetSchema
{
    public ParquetSchema(ImmutableArray<Column> columns)
    {
        Columns = columns.IsDefault ? [] : columns;
        Definitions = NormalizeDefinitions(Columns);
    }

    public ParquetSchema(ImmutableArray<ColumnDefinition> definitions)
    {
        Definitions = definitions.IsDefault ? [] : definitions;
        Columns = TryProjectFlatColumns(Definitions, out var projected) ? projected : [];
    }

    public ImmutableArray<Column> Columns { get; }

    public ImmutableArray<ColumnDefinition> Definitions { get; }

    public void Validate()
    {
        ValidateDefinitions();

        if (Columns.IsDefaultOrEmpty)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in Columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            column.Validate();
            if (!seen.Add(column.Name))
                throw new InvalidOperationException($"Duplicate column name '{column.Name}' is not allowed.");
        }
    }

    void ValidateDefinitions()
    {
        if (Definitions.IsDefaultOrEmpty)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Definitions.Length; i++)
        {
            var definition = Definitions[i];
            ArgumentNullException.ThrowIfNull(definition);
            definition.Validate();
            if (!seen.Add(definition.Name))
                throw new InvalidOperationException($"Duplicate root node name '{definition.Name}' is not allowed.");
        }
    }

    static ImmutableArray<ColumnDefinition> NormalizeDefinitions(ImmutableArray<Column> columns)
    {
        if (columns.IsDefaultOrEmpty)
            return [];

        var builder = ImmutableArray.CreateBuilder<ColumnDefinition>(columns.Length);
        foreach (var column in columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            builder.Add(new ColumnDefinition
            {
                Name = column.Name,
                Kind = NodeKind.Leaf,
                Repetition = column.Options.Repetition == ParquetRepetition.Unspecified
                    ? ParquetRepetition.Required
                    : column.Options.Repetition,
                PhysicalType = column.PhysicalType,
                Options = column.Options
            });
        }

        return builder.MoveToImmutable();
    }

    static bool TryProjectFlatColumns(ImmutableArray<ColumnDefinition> definitions, out ImmutableArray<Column> columns)
    {
        if (definitions.IsDefaultOrEmpty)
        {
            columns = [];
            return true;
        }

        var builder = ImmutableArray.CreateBuilder<Column>(definitions.Length);
        for (var i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i];
            if (definition.Kind != NodeKind.Leaf || definition.PhysicalType is null || !definition.Children.IsDefaultOrEmpty)
            {
                columns = [];
                return false;
            }

            var repetition = definition.Repetition == ParquetRepetition.Unspecified
                ? ParquetRepetition.Required
                : definition.Repetition;
            var options = definition.Options ?? ColumnOptions.Default;
            if (options.Repetition != repetition)
                options = new ColumnOptions(repetition, options.Encodings, options.TypeLength);

            builder.Add(new Column(definition.Name, definition.PhysicalType.Value, options));
        }

        columns = builder.MoveToImmutable();
        return true;
    }
}
