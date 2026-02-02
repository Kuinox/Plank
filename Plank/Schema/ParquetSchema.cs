using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

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

    public static ParquetSchema Create(params Column[] columns)
    {
        if (columns is null)
            throw new ArgumentNullException(nameof(columns));

        var copy = new Column[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            copy[i] = columns[i] ?? throw new ArgumentNullException(nameof(columns), $"Column at index {i} is null.");
        }

        for (var i = 0; i < copy.Length; i++)
        {
            if (copy[i].Ordinal != i)
                throw new ArgumentException($"Column '{copy[i].Name}' has ordinal {copy[i].Ordinal} but is at index {i}.", nameof(columns));
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
            throw new ArgumentNullException(nameof(schema));

        var type = typeof(T);
        Registry.AddOrUpdate(type, schema, (_, existing) =>
        {
            if (!ReferenceEquals(existing, schema))
                throw new InvalidOperationException($"A schema is already registered for {type}.");

            return existing;
        });
    }

    public static ParquetSchema For<T>(Action<RowSchemaBuilder<T>> configure)
    {
        if (TryGet<T>(out var schema))
            return schema;

        throw new ParquetSchemaNotGeneratedException(typeof(T));
    }

    internal static bool TryGet<T>([NotNullWhen(true)] out ParquetSchema? schema)
    {
        return Registry.TryGetValue(typeof(T), out schema);
    }
}
