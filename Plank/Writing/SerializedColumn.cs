namespace Plank;

public readonly struct SerializedColumn
{
    readonly RowGroupWriter _group;
    readonly int _ordinal;

    internal SerializedColumn(RowGroupWriter group, int ordinal)
    {
        _group = group;
        _ordinal = ordinal;
    }

    public ValueTask WriteAsync(CancellationToken cancellationToken = default)
    {
        return _group.WriteSerializedAsync(_ordinal, cancellationToken);
    }
}
