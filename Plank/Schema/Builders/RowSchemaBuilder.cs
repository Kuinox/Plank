using System.Linq.Expressions;

namespace Plank;

public sealed class RowSchemaBuilder<T>
{
    public RowColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return new RowColumnBuilder<T, TProp>(expression);
    }

    public RowColumnBuilder<T, TProp> Column<TProp>(Expression<Func<T, TProp>> expression, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Column(expression).Name(name);
    }
}
