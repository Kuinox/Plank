namespace Plank.Writing;

public readonly struct SerializedColumn : IEquatable<SerializedColumn>
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
        => _group.Equals(other._group)
           && _ordinal == other._ordinal;

    public override bool Equals(object? obj)
        => obj is SerializedColumn other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(_group, _ordinal);

    public static bool operator ==(SerializedColumn left, SerializedColumn right)
        => left.Equals(right);

    public static bool operator !=(SerializedColumn left, SerializedColumn right)
        => !left.Equals(right);
}
