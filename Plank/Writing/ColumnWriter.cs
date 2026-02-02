namespace Plank;

public readonly struct ColumnWriter
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
        => throw new NotImplementedException();

    public override bool Equals(object? obj)
        => throw new NotImplementedException();

    public override int GetHashCode()
        => throw new NotImplementedException();

    public static bool operator ==(ColumnWriter left, ColumnWriter right)
        => throw new NotImplementedException();

    public static bool operator !=(ColumnWriter left, ColumnWriter right)
        => throw new NotImplementedException();
}
