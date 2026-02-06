namespace Plank.Writing;

public readonly ref struct RepeatedValues<T>
{
    readonly ReadOnlySpan<T[]> _rows;

    public RepeatedValues(ReadOnlySpan<T[]> rows)
        => _rows = rows;

    internal ReadOnlySpan<T[]> Rows
        => _rows;
}
