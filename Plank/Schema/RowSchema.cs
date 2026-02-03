namespace Plank.Schema;

public sealed record RowSchema<T>(IReadOnlyList<RowColumnDefinition<T>> Columns);
