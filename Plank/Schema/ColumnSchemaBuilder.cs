namespace Plank;

public sealed class ColumnSchemaBuilder<TProp>
{
    readonly ParquetSchemaBuilder _schema;
    readonly ColumnDefinition<TProp> _definition;

    internal ColumnSchemaBuilder(ParquetSchemaBuilder schema, ColumnDefinition<TProp> definition)
    {
        _schema = schema;
        _definition = definition;
    }

    public ColumnSchemaBuilder<TProp> Name(string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));

        _definition.Name = name;
        return this;
    }

    public ColumnSchemaBuilder<TProp> Encoding(EncodingKind encoding)
    {
        _definition.Options = _definition.Options.WithEncoding(encoding);
        return this;
    }

    public ColumnSchemaBuilder<TProp> Encodings(params EncodingKind[] encodings)
    {
        if (encodings is null)
            throw new ArgumentNullException(nameof(encodings));

        var options = _definition.Options;
        for (var i = 0; i < encodings.Length; i++)
        {
            options = options.WithEncoding(encodings[i]);
        }

        _definition.Options = options;
        return this;
    }

    public ColumnSchemaBuilder<TProp> Optional()
    {
        _definition.Options = _definition.Options.WithRepetition(ParquetRepetition.Optional);
        return this;
    }

    public ColumnSchemaBuilder<TProp> Required()
    {
        _definition.Options = _definition.Options.WithRepetition(ParquetRepetition.Required);
        return this;
    }

    public ColumnSchemaBuilder<TNext> Column<TNext>(string name)
    {
        return _schema.Column<TNext>(name);
    }

    public ParquetSchema Build()
    {
        return _schema.Build();
    }
}
