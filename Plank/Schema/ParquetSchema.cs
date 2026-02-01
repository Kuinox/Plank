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

        return new ParquetSchema(copy);
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
