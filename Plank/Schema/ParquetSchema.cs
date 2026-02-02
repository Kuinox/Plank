using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Plank;

public sealed class ParquetSchema
{
    private static readonly ConcurrentDictionary<Type, ParquetSchema> Registry = new();
    private readonly IColumn[] _columns;

    private ParquetSchema(IColumn[] columns)
    {
        _columns = columns;
    }

    public IReadOnlyList<IColumn> Columns => _columns;

    public static ParquetSchema Create(params IColumn[] columns)
    {
        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        var copy = new IColumn[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            copy[i] = columns[i] ?? throw new ArgumentNullException(nameof(columns), $"Column at index {i} is null.");
        }

        for (var i = 0; i < copy.Length; i++)
        {
            if (copy[i].Ordinal != i)
            {
                throw new ArgumentException($"Column '{copy[i].Name}' has ordinal {copy[i].Ordinal} but is at index {i}.", nameof(columns));
            }
        }

        return new ParquetSchema(copy);
    }

    public static ColumnSchemaBuilder Define()
    {
        return new ColumnSchemaBuilder();
    }

    public static void Register<T>(ParquetSchema schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        var type = typeof(T);
        Registry.AddOrUpdate(type, schema, (_, existing) =>
        {
            if (!ReferenceEquals(existing, schema))
            {
                throw new InvalidOperationException($"A schema is already registered for {type}.");
            }

            return existing;
        });
    }

    public static ParquetSchema For<T>(Action<SchemaBuilder<T>> configure)
    {
        if (TryGet<T>(out var schema))
        {
            return schema;
        }

        throw new ParquetSchemaNotGeneratedException(typeof(T));
    }

    internal static bool TryGet<T>([NotNullWhen(true)] out ParquetSchema? schema)
    {
        return Registry.TryGetValue(typeof(T), out schema);
    }
}

public sealed class SchemaBuilder<T>
{
    public ColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression is null)
        {
            throw new ArgumentNullException(nameof(expression));
        }

        return new ColumnBuilder<T, TProp>(expression);
    }

    public ColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression, string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return Column(expression).Name(name);
    }
}

public sealed class ColumnSchemaBuilder
{
    private readonly List<IColumnDefinition> _definitions = new();

    public ColumnDefinitionBuilder<TProp> Column<TProp>(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        var definition = new ColumnDefinition<TProp>(name);
        _definitions.Add(definition);
        return new ColumnDefinitionBuilder<TProp>(this, definition);
    }

    public ParquetSchema Build()
    {
        var columns = new IColumn[_definitions.Count];
        for (var i = 0; i < _definitions.Count; i++)
        {
            columns[i] = _definitions[i].Create(i);
        }

        return Create(columns);
    }
}

public sealed class ColumnDefinitionBuilder<TProp>
{
    private readonly ColumnSchemaBuilder _schema;
    private readonly ColumnDefinition<TProp> _definition;

    internal ColumnDefinitionBuilder(ColumnSchemaBuilder schema, ColumnDefinition<TProp> definition)
    {
        _schema = schema;
        _definition = definition;
    }

    public ColumnDefinitionBuilder<TProp> Name(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        _definition.Name = name;
        return this;
    }

    public ColumnDefinitionBuilder<TProp> Encoding(EncodingKind encoding)
    {
        _definition.Options = _definition.Options.WithEncoding(encoding);
        return this;
    }

    public ColumnDefinitionBuilder<TProp> Encodings(params EncodingKind[] encodings)
    {
        if (encodings is null)
        {
            throw new ArgumentNullException(nameof(encodings));
        }

        var options = _definition.Options;
        for (var i = 0; i < encodings.Length; i++)
        {
            options = options.WithEncoding(encodings[i]);
        }

        _definition.Options = options;
        return this;
    }

    public ColumnDefinitionBuilder<TProp> Optional()
    {
        _definition.Options = _definition.Options.WithRepetition(ParquetRepetition.Optional);
        return this;
    }

    public ColumnDefinitionBuilder<TProp> Required()
    {
        _definition.Options = _definition.Options.WithRepetition(ParquetRepetition.Required);
        return this;
    }

    public ColumnDefinitionBuilder<TProp> Column<TNext>(string name)
    {
        return _schema.Column<TNext>(name);
    }

    public ParquetSchema Build()
    {
        return _schema.Build();
    }
}

internal interface IColumnDefinition
{
    IColumn Create(int ordinal);
}

internal sealed class ColumnDefinition<TProp> : IColumnDefinition
{
    public ColumnDefinition(string name)
    {
        Name = name;
        Options = ColumnOptions.Default;
    }

    public string Name { get; set; }

    public ColumnOptions Options { get; set; }

    public IColumn Create(int ordinal)
    {
        return new Column<TProp>(ordinal, Name, Options);
    }
}

public sealed class ColumnBuilder<T, TProp>
{
    internal ColumnBuilder(Expression<Func<T, TProp>> expression)
    {
        Expression = expression;
    }

    internal Expression<Func<T, TProp>> Expression { get; }

    public ColumnBuilder<T, TProp> Name(string name)
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Encoding(EncodingKind encoding)
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Encodings(params EncodingKind[] encodings)
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Optional()
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Required()
    {
        return this;
    }
}

public sealed class ParquetSchemaNotGeneratedException : InvalidOperationException
{
    public ParquetSchemaNotGeneratedException(Type type)
        : base($"No generated Parquet schema was registered for {type}.")
    {
        Type = type;
    }

    public Type Type { get; }
}
