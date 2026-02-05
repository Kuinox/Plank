using System.Threading;
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

    public ValueTask WriteAsync<T>(Column column, ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!_state.ColumnOrdinals.TryGetValue(column, out var ordinal))
            throw new ArgumentException("Column does not belong to this schema.", nameof(column));

        if (column.Options.Repetition is ParquetRepetition.Optional or ParquetRepetition.Repeated)
            throw new NotSupportedException($"Column '{column.Name}' requires definition/repetition levels which are not supported yet.");

        var physicalType = ParquetTypeMap.GetPhysicalType(typeof(T));
        if (column.PhysicalType != physicalType)
            throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");

        EncodeColumn(column, values, physicalType, ordinal);
        return WriteSerializedAsync(ordinal, cancellationToken);
    }

    void EncodeColumn<T>(Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType, int ordinal)
    {
        ref var columnState = ref _state.ColumnStates[ordinal];
        if (Interlocked.CompareExchange(ref columnState.WriteState, ParquetWriter.RowGroupState.WriteStateEncoded, ParquetWriter.RowGroupState.WriteStateEmpty) != ParquetWriter.RowGroupState.WriteStateEmpty)
            throw new InvalidOperationException($"Column '{column.Name}' was already written.");

        try
        {
            columnState.ValueCount = values.Length;
            ColumnCodec.Encode(column, values, physicalType, _state.Options, ref columnState);
            ColumnCodec.Compress(ref columnState);
        }
        catch
        {
            columnState.ValueCount = 0;
            columnState.EncodedLength = 0;
            columnState.UncompressedLength = 0;
            Volatile.Write(ref columnState.WriteState, ParquetWriter.RowGroupState.WriteStateEmpty);
            throw;
        }
    }

    internal async ValueTask WriteSerializedAsync(int ordinal, CancellationToken cancellationToken)
    {
        if (ordinal < 0 || ordinal >= _state.ColumnStates.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        await _state.WaitForTurnAsync(ordinal, cancellationToken).ConfigureAwait(false);
        await _writer.EnsureHeaderWrittenAsync(cancellationToken).ConfigureAwait(false);

        var columnState = _state.ColumnStates[ordinal];
        var valueCount = columnState.ValueCount;
        var rowCount = _state.RowCount;
        if (rowCount < 0)
        {
            _state.RowCount = valueCount;
            rowCount = valueCount;
        }

        if (rowCount != valueCount)
            throw new InvalidOperationException($"Column ordinal {ordinal} has {valueCount} values but row group expects {rowCount}.");

        var length = columnState.EncodedLength;
        var offset = _writer.Position;
        var headerLength = ParquetThriftWriter.WriteDataPageHeader(
            _state.PageHeaderBuffer,
            valueCount,
            columnState.Encoding,
            columnState.UncompressedLength,
            length);
        await _writer.Stream.WriteAsync(_state.PageHeaderBuffer.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
        _writer.AdvancePosition(headerLength);

        if (length > 0)
        {
            var buffer = columnState.EncodedBuffer;
            if (buffer is null)
                throw new InvalidOperationException($"Column ordinal {ordinal} has no serialized buffer.");

            await _writer.Stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            _writer.AdvancePosition(length);
        }

        FinishWrite(rowCount, ordinal, offset, headerLength);
    }

    void FinishWrite(int rowCount, int ordinal, long offset, int headerLength)
    {
        ref var columnState = ref _state.ColumnStates[ordinal];
        var totalUncompressedSize = checked((long)columnState.UncompressedLength + headerLength);
        var totalCompressedSize = checked((long)columnState.EncodedLength + headerLength);
        _state.ColumnMetadata[ordinal] = new ParquetWriter.ColumnChunkMetadata(
            offset,
            columnState.ValueCount,
            totalUncompressedSize,
            totalCompressedSize,
            columnState.Encoding,
            columnState.Compression);
        columnState.ValueCount = 0;
        columnState.EncodedLength = 0;
        columnState.UncompressedLength = 0;
        var nextOrdinal = _state.AdvanceOrdinal();
        if (nextOrdinal == _state.ColumnStates.Length)
            _writer.CompleteRowGroup(rowCount);
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
