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

        var next = _definition with { Name = name };
        _schema.ReplaceDefinition(_index, next);
        return new ColumnSchemaBuilder<TProp>(_schema, _index, next);
    }

    public ColumnSchemaBuilder<TProp> Encoding(EncodingKind encoding)
    {
        var next = _definition with { Options = _definition.Options.WithEncoding(encoding) };
        _schema.ReplaceDefinition(_index, next);
        return new ColumnSchemaBuilder<TProp>(_schema, _index, next);
    }

    public ColumnSchemaBuilder<TProp> Encodings(params EncodingKind[] encodings)
    {
        ArgumentNullException.ThrowIfNull(encodings);

        var options = _definition.Options;
        for (var i = 0; i < encodings.Length; i++)
            options = options.WithEncoding(encodings[i]);

        var next = _definition with { Options = options };
        _schema.ReplaceDefinition(_index, next);
        return new ColumnSchemaBuilder<TProp>(_schema, _index, next);
    }

    public ColumnSchemaBuilder<TProp> Optional()
    {
        var next = _definition with { Options = _definition.Options.WithRepetition(ParquetRepetition.Optional) };
        _schema.ReplaceDefinition(_index, next);
        return new ColumnSchemaBuilder<TProp>(_schema, _index, next);
    }

    public ColumnSchemaBuilder<TProp> Required()
    {
        var next = _definition with { Options = _definition.Options.WithRepetition(ParquetRepetition.Required) };
        _schema.ReplaceDefinition(_index, next);
        return new ColumnSchemaBuilder<TProp>(_schema, _index, next);
    }

    public ColumnSchemaBuilder<TNext> Column<TNext>(string name)
        => _schema.Column<TNext>(name);

    public ParquetSchema Build()
        => _schema.Build();
}
