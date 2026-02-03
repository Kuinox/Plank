using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Plank.Schema;

public sealed partial class ParquetSchema
{
    static readonly ConcurrentDictionary<Type, ParquetSchema> Registry = new();
    readonly Column[] _columns;

    public ParquetSchema(params ColumnDefinition[] columns)
        => _columns = Validate(columns);

    public IReadOnlyList<Column> Columns => _columns;

    public static Column[] Validate(params ColumnDefinition[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var resolved = new Column[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i] is null)
                throw new ArgumentNullException(nameof(columns), $"Column at index {i} is null.");

            var definition = columns[i]!;
            ArgumentNullException.ThrowIfNull(definition.Name);
            ArgumentNullException.ThrowIfNull(definition.ClrType);

            var physicalType = ParquetTypeMap.GetPhysicalType(definition.ClrType);
            var repetition = definition.Options.Repetition;
            if (repetition == ParquetRepetition.Unspecified)
                repetition = ParquetTypeMap.IsNullable(definition.ClrType)
                    ? ParquetRepetition.Optional
                    : ParquetRepetition.Required;

            var encodings = definition.Options.Encodings.IsDefault
                ? ImmutableArray<EncodingKind>.Empty
                : definition.Options.Encodings;

            resolved[i] = new Column(i, definition.Name, definition.ClrType, physicalType, repetition, encodings);
        }

        return resolved;
    }

    public static void Register<T>(ParquetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var type = typeof(T);
        Registry.AddOrUpdate(type, schema, (_, existing) =>
        {
            if (!ReferenceEquals(existing, schema))
                throw new InvalidOperationException($"A schema is already registered for {type}.");

            return existing;
        });
    }

    public static ParquetSchema For<T>(RowSchema<T> rowSchema)
    {
        _ = rowSchema;

        if (TryGet<T>(out var schema))
            return schema;

        throw new InvalidOperationException($"No generated Parquet schema was registered for {typeof(T)}.");
    }

    internal static bool TryGet<T>([NotNullWhen(true)] out ParquetSchema? schema)
        => Registry.TryGetValue(typeof(T), out schema);
}
