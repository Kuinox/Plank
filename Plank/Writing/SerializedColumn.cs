namespace Plank;

public readonly struct SerializedColumn
{
    private readonly RowGroupWriter _group;
    private readonly int _ordinal;

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
