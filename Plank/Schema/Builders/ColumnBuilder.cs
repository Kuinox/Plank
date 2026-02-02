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
        => this;

    public ColumnBuilder<T, TProp> Encoding(EncodingKind encoding)
        => this;

    public ColumnBuilder<T, TProp> Encodings(params EncodingKind[] encodings)
        => this;

    public ColumnBuilder<T, TProp> Optional()
        => this;

    public ColumnBuilder<T, TProp> Required()
        => this;
}
