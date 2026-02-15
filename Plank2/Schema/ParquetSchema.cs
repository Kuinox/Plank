using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ParquetSchema(ImmutableArray<Column> Columns)
{
    public void Validate()
    {
        if (Columns.IsDefault || Columns.Length == 0)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in Columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            column.Validate();
            if (!seen.Add(column.Name))
                throw new InvalidOperationException($"Duplicate column name '{column.Name}' is not allowed.");
        }
    }

}
