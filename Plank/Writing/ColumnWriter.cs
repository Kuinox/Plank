namespace Plank;

public readonly struct ColumnWriter : IEquatable<ColumnWriter>
{
    readonly RowGroupWriter _group;
    readonly ParquetSchema.Column _column;

    internal ColumnWriter(RowGroupWriter group, ParquetSchema.Column column)
    {
        _group = group;
        _column = column;
    }

    public SerializedColumn Serialize<T>(ReadOnlySpan<T> values)
        => _group.Serialize(_column, values, null, null);

    public ValueTask WriteAsync<T>(ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
        => Serialize(values).WriteAsync(cancellationToken);

    public bool Equals(ColumnWriter other)
        => ReferenceEquals(_group, other._group)
           && ReferenceEquals(_column, other._column);

    public override bool Equals(object? obj)
        => obj is ColumnWriter other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(_group, _column);

    public static bool operator ==(ColumnWriter left, ColumnWriter right)
        => left.Equals(right);

    public static bool operator !=(ColumnWriter left, ColumnWriter right)
        => !left.Equals(right);
}
