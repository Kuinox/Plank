namespace Plank.Schema;

public sealed record RowSchema<T>(IReadOnlyList<RowColumnDefinition<T>> Columns)
{
    public static RowSchema<T> Create(params RowColumnDefinition<T>[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new RowSchema<T>(columns);
    }
}
