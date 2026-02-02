using System.Linq.Expressions;

namespace Plank.Schema;

public sealed record RowColumnDefinition<T>(LambdaExpression Expression, Type ClrType, ColumnOptions Options)
{
    public string? Name { get; init; }

    public static RowColumnDefinition<T> Create<TProp>(Expression<Func<T, TProp>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new RowColumnDefinition<T>(expression, typeof(TProp), ColumnOptions.Default);
    }
}
