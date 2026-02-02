namespace Plank.Schema.Builders;

public sealed class ColumnSchemaBuilder<TProp>
{
    readonly ParquetSchemaBuilder _schema;
    readonly int _index;
    readonly ColumnDefinition _definition;

    internal ColumnSchemaBuilder(ParquetSchemaBuilder schema, int index, ColumnDefinition definition)
    {
        _schema = schema;
        _index = index;
        _definition = definition;
    }

    public ColumnSchemaBuilder<TProp> Name(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return With(_definition with { Name = name });
    }

    public ColumnSchemaBuilder<TProp> Encoding(EncodingKind encoding)
        => With(_definition with { Options = _definition.Options.WithEncoding(encoding) });

    public ColumnSchemaBuilder<TProp> Encodings(params EncodingKind[] encodings)
    {
        ArgumentNullException.ThrowIfNull(encodings);

        var options = _definition.Options;
        for (var i = 0; i < encodings.Length; i++)
            options = options.WithEncoding(encodings[i]);

        return With(_definition with { Options = options });
    }

    public ColumnSchemaBuilder<TProp> Optional()
        => With(_definition with { Options = _definition.Options.WithRepetition(ParquetRepetition.Optional) });

    public ColumnSchemaBuilder<TProp> Required()
        => With(_definition with { Options = _definition.Options.WithRepetition(ParquetRepetition.Required) });

    public ColumnSchemaBuilder<TNext> Column<TNext>(string name)
        => _schema.Column<TNext>(name);

    public ParquetSchema Build()
        => _schema.Build();

    ColumnSchemaBuilder<TProp> With(ColumnDefinition definition)
    {
        _schema.ReplaceDefinition(_index, definition);
        return new ColumnSchemaBuilder<TProp>(_schema, _index, definition);
    }
}
