using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ParquetSchema(ImmutableArray<Column> Columns)
{
    public void Validate()
    {
        if (Columns.IsDefault)
            throw new InvalidOperationException("Columns must be initialized.");

        for (var i = 0; i < Columns.Length; i++)
        {
            var column = Columns[i];
            if (column is null)
                throw new InvalidOperationException($"Column at index {i} is null.");

            ArgumentNullException.ThrowIfNull(column.Name);
            ArgumentNullException.ThrowIfNull(column.ClrType);
        }
    }

    public static ParquetSchema For<T>(RowSchema<T> rowSchema)
    {
        ArgumentNullException.ThrowIfNull(rowSchema);
        throw new InvalidOperationException($"No generated Parquet schema was registered for {typeof(T)}.");
    }
}
