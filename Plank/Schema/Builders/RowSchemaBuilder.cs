using System.Linq.Expressions;

namespace Plank;

public sealed class RowSchemaBuilder<T>
{
    public RowColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));

        return new RowColumnBuilder<T, TProp>(expression);
    }

    public RowColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression, string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));

        return Column(expression).Name(name);
    }
}
