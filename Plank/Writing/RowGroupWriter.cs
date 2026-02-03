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

    public int RowCount
        => _state.RowCount;

    public SerializedColumn Serialize<T>(Column column, ReadOnlySpan<T> values)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!_state.ColumnOrdinals.TryGetValue(column, out var ordinal))
            throw new ArgumentException("Column does not belong to this schema.", nameof(column));

        var physicalType = ParquetTypeMap.GetPhysicalType(typeof(T));
        if (column.PhysicalType != physicalType)
            throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");

        ref var columnState = ref _state.ColumnStates[ordinal];
        columnState.ValueCount = values.Length;
        ColumnCodec.Encode(column, values, physicalType, _state.Options, ref columnState);
        ColumnCodec.Compress(ref columnState);

        return new SerializedColumn(this, ordinal);
    }


    internal ValueTask WriteSerializedAsync(int ordinal, CancellationToken cancellationToken)
    {
        if (ordinal < 0 || ordinal >= _state.ColumnStates.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        ref var columnState = ref _state.ColumnStates[ordinal];
        var valueCount = columnState.ValueCount;
        var rowCount = _state.RowCount;
        if (rowCount < 0)
        {
            _state.RowCount = valueCount;
            rowCount = valueCount;
        }

        if (rowCount != valueCount)
            throw new InvalidOperationException($"Column ordinal {ordinal} has {valueCount} values but row group expects {rowCount}.");

        if (ordinal != _state.NextOrdinal)
            throw new InvalidOperationException($"Column ordinal {ordinal} was written out of order. Expected {_state.NextOrdinal}.");

        var buffer = columnState.EncodedBuffer;
        var length = columnState.EncodedLength;
        if (length == 0)
        {
            FinishWrite(rowCount, ordinal);
            return ValueTask.CompletedTask;
        }

        if (buffer is null)
            throw new InvalidOperationException($"Column ordinal {ordinal} has no serialized buffer.");

        var writeTask = _writer.Stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
        if (writeTask.IsCompletedSuccessfully)
        {
            FinishWrite(rowCount, ordinal);
            return ValueTask.CompletedTask;
        }

        return AwaitWriteAsync(writeTask, rowCount, ordinal);
    }

    void FinishWrite(int rowCount, int ordinal)
    {
        ref var columnState = ref _state.ColumnStates[ordinal];
        columnState.ValueCount = 0;
        columnState.EncodedLength = 0;
        _state.NextOrdinal++;
        if (_state.NextOrdinal == _state.ColumnStates.Length)
            _writer.CompleteRowGroup(rowCount);
    }

    async ValueTask AwaitWriteAsync(ValueTask writeTask, int rowCount, int ordinal)
    {
        await writeTask.ConfigureAwait(false);
        FinishWrite(rowCount, ordinal);
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
