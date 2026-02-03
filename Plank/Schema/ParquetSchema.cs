namespace Plank.Schema;

public sealed record ParquetSchema
{
    readonly Column[] _columns;

    public ParquetSchema(params Column[] columns)
        => _columns = Validate(columns);

    public IReadOnlyList<Column> Columns => _columns;

    internal Column[] ColumnArray => _columns;

    public static Column[] Validate(params Column[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var resolved = new Column[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i] is null)
                throw new ArgumentNullException(nameof(columns), $"Column at index {i} is null.");

            var column = columns[i]!;
            ArgumentNullException.ThrowIfNull(column.Name);
            ArgumentNullException.ThrowIfNull(column.ClrType);

            resolved[i] = column;
        }

        return resolved;
    }

    public static ParquetSchema For<T>(RowSchema<T> rowSchema)
    {
        ArgumentNullException.ThrowIfNull(rowSchema);
        throw new InvalidOperationException($"No generated Parquet schema was registered for {typeof(T)}.");
    }
}
