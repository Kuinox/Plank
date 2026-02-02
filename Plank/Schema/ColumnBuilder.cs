using System.Linq.Expressions;

namespace Plank;

public sealed class ColumnBuilder<T, TProp>
{
    internal ColumnBuilder(Expression<Func<T, TProp>> expression)
    {
        Expression = expression;
    }

    internal Expression<Func<T, TProp>> Expression { get; }

    public ColumnBuilder<T, TProp> Name(string name)
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Encoding(EncodingKind encoding)
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Encodings(params EncodingKind[] encodings)
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Optional()
    {
        return this;
    }

    public ColumnBuilder<T, TProp> Required()
    {
        return this;
    }
}
