using System.Linq.Expressions;

namespace Plank.Schema;

public sealed record RowColumnDefinition<T>(LambdaExpression Expression, Type ClrType, ColumnOptions Options)
{
    public string? Name { get; init; }
}
