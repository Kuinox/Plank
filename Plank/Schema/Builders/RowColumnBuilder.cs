using System.Linq.Expressions;

namespace Plank;

public sealed class RowColumnBuilder<T, TProp>
{
    internal RowColumnBuilder(Expression<Func<T, TProp>> expression)
    {
        Expression = expression;
    }

    internal Expression<Func<T, TProp>> Expression { get; }

    public RowColumnBuilder<T, TProp> Name(string name)
        => this;

    public RowColumnBuilder<T, TProp> Encoding(EncodingKind encoding)
        => this;

    public RowColumnBuilder<T, TProp> Encodings(params EncodingKind[] encodings)
        => this;

    public RowColumnBuilder<T, TProp> Optional()
        => this;

    public RowColumnBuilder<T, TProp> Required()
        => this;
}
