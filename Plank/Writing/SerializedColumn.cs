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
        => _group.WriteSerializedAsync(_ordinal, cancellationToken);

    public bool Equals(SerializedColumn other)
        => throw new NotImplementedException();

    public override bool Equals(object? obj)
        => throw new NotImplementedException();

    public override int GetHashCode()
        => throw new NotImplementedException();

    public static bool operator ==(SerializedColumn left, SerializedColumn right)
        => throw new NotImplementedException();

    public static bool operator !=(SerializedColumn left, SerializedColumn right)
        => throw new NotImplementedException();
}
