using System.Linq.Expressions;

namespace Plank;

public sealed class SchemaBuilder<T>
{
    public ColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));

        return new ColumnBuilder<T, TProp>(expression);
    }

    public ColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression, string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));

        return Column(expression).Name(name);
    }
}
