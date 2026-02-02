using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Plank;

public sealed partial class ParquetSchema
{
    static readonly ConcurrentDictionary<Type, ParquetSchema> Registry = new();
    readonly Column[] _columns;

    ParquetSchema(Column[] columns)
    {
        _columns = columns;
    }

    public IReadOnlyList<Column> Columns => _columns;

    public Column ColumnAt(int ordinal)
    {
        if ((uint)ordinal >= (uint)_columns.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal is out of range.");
        }

        return _columns[ordinal];
    }

    public Column<TProp> ColumnAt<TProp>(int ordinal)
    {
        var column = ColumnAt(ordinal);
        if (column is Column<TProp> typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"Column at ordinal {ordinal} is not of type {typeof(TProp)}.");
    }

    public static ParquetSchema Create(params Column[] columns)
    {
        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        var copy = new Column[columns.Length];
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

    public static ParquetSchemaBuilder Define()
    {
        return new ParquetSchemaBuilder();
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

public sealed class ParquetSchemaBuilder
{
    readonly List<IColumnDefinition> _definitions = new();

    public ColumnSchemaBuilder<TProp> Column<TProp>(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        var definition = new ColumnDefinition<TProp>(name);
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
        {
            throw new ArgumentNullException(nameof(name));
        }

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

internal interface IColumnDefinition
{
    ParquetSchema.Column Create(int ordinal);
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

    public ParquetSchema.Column Create(int ordinal)
    {
        return new ParquetSchema.Column<TProp>(ordinal, Name, Options);
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
