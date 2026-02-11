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

        _writer.RegisterValueType(column, typeof(T));
        var physicalType = GetPhysicalType<T>();
        var ordinal = _state.EncodeColumn(column, values, physicalType);
        _state.TryDrain(_writer);
        return _state.GetWriteTask(ordinal, cancellationToken);
    }

    public ValueTask WriteAsync<T>(Column column, ReadOnlySpan<T[]> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        int ordinal;
        if (column.Options.Repetition is ParquetRepetition.Repeated)
        {
            _writer.RegisterValueType(column, typeof(T));
            var physicalType = GetPhysicalType<T>();
            ordinal = _state.EncodeRepeatedColumn(column, rows, physicalType);
        }
        else
        {
            _writer.RegisterValueType(column, typeof(T[]));
            var resolution = ParquetTypeMap.ResolvePhysicalType<T[]>();
            if (!resolution.IsSuccess)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' is not Repeated and does not accept values of type '{typeof(T[])}'.");

            var physicalType = resolution.PhysicalType;
            ordinal = _state.EncodeColumn(column, rows, physicalType);
        }

        _state.TryDrain(_writer);
        return _state.GetWriteTask(ordinal, cancellationToken);
    }

    public ValueTask WriteAsync(ParquetWriter.SerializedColumn serialized, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        ArgumentNullException.ThrowIfNull(serialized.Column);
        if (!_state.TryGetOrdinal(serialized.Column, out var ordinal))
            throw new ArgumentException("Column does not belong to this schema.", nameof(serialized));

        _writer.RegisterSerializedColumnType(ordinal, serialized.Column, serialized.LogicalType, serialized.RepeatedElementOptional);
        ordinal = _state.AcceptSerialized(ordinal, serialized);
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

    static ParquetPhysicalType GetPhysicalType<T>()
        => ParquetTypeMap.GetPhysicalType<T>();
}
