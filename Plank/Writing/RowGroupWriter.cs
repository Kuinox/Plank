using Plank.Schema;

namespace Plank.Writing;

public readonly struct RowGroupWriter : IEquatable<RowGroupWriter>
{
    readonly ParquetWriter _writer;
    readonly ParquetWriter.RowGroupState _state;

    internal RowGroupWriter(ParquetWriter writer, ParquetWriter.RowGroupState state)
    {
        _writer = writer;
        _state = state;
    }

    public ValueTask WriteAsync<T>(Column column, ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        var physicalType = ParquetTypeMap.GetPhysicalType(typeof(T));
        var ordinal = _state.EncodeColumn(column, values, physicalType);
        _state.TryDrain(_writer);
        return _state.GetWriteTask(ordinal, cancellationToken);
    }

    public bool Equals(RowGroupWriter other)
        => ReferenceEquals(_state, other._state)
           && ReferenceEquals(_writer, other._writer);

    public override bool Equals(object? obj)
        => obj is RowGroupWriter other && Equals(other);

    public override int GetHashCode()
        => _state.GetHashCode();

    public static bool operator ==(RowGroupWriter left, RowGroupWriter right)
        => left.Equals(right);

    public static bool operator !=(RowGroupWriter left, RowGroupWriter right)
        => !left.Equals(right);
}
