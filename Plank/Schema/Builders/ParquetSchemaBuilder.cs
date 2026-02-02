namespace Plank.Schema.Builders;

public sealed class ParquetSchemaBuilder
{
    readonly List<ColumnDefinition> _definitions = new();

    public ColumnSchemaBuilder<TProp> Column<TProp>(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var definition = new ColumnDefinition(name, typeof(TProp), ColumnOptions.Default);
        _definitions.Add(definition);
        return new ColumnSchemaBuilder<TProp>(this, _definitions.Count - 1, definition);
    }

    public ParquetSchema Build()
    {
        var columns = new ParquetSchema.Column[_definitions.Count];
        for (var i = 0; i < _definitions.Count; i++)
        {
            columns[i] = _definitions[i].Create(i);
        }

        return ParquetSchema.Create(columns);
    }

    internal void ReplaceDefinition(int index, ColumnDefinition definition)
        => _definitions[index] = definition;
}
