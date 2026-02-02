namespace Plank;

public sealed class ColumnSchemaBuilder<TProp>
{
    readonly ParquetSchemaBuilder _schema;
    readonly ColumnDefinition _definition;

    internal ColumnSchemaBuilder(ParquetSchemaBuilder schema, ColumnDefinition definition)
    {
        _schema = schema;
        _definition = definition;
    }

    public ColumnSchemaBuilder<TProp> Name(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

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
        ArgumentNullException.ThrowIfNull(encodings);

        var options = _definition.Options;
        for (var i = 0; i < encodings.Length; i++)
            options = options.WithEncoding(encodings[i]);

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
        => _schema.Column<TNext>(name);

    public ParquetSchema Build()
        => _schema.Build();
}
