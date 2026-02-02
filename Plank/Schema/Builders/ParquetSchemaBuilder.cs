namespace Plank;

public sealed class ParquetSchemaBuilder
{
    readonly List<ColumnDefinition> _definitions = new();

    public ColumnSchemaBuilder<TProp> Column<TProp>(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var definition = new ColumnDefinition(name, typeof(TProp));
        _definitions.Add(definition);
        return new ColumnSchemaBuilder<TProp>(this, definition);
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
}
