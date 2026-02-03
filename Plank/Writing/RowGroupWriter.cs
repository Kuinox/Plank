using System.Collections.Immutable;
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
        => SerializeCore(column, values);


    SerializedColumn SerializeCore<T>(Column column, ReadOnlySpan<T> values)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!_state.ColumnOrdinals.TryGetValue(column, out var ordinal))
            throw new ArgumentException("Column does not belong to this schema.", nameof(column));
        ref var columnState = ref _state.ColumnStates[ordinal];

        if (column.ClrType != typeof(T))
            throw new InvalidOperationException($"Column '{column.Name}' expects {column.ClrType}, but received {typeof(T)}.");

        columnState.ValueCount = values.Length;
        columnState.Encoding = ResolveDefaultEncoding(column.Options.Encodings);
        columnState.Compression = CompressionKind.None;

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
        _state.NextOrdinal++;

        columnState.ValueCount = 0;
        if (_state.NextOrdinal == _state.ColumnStates.Length)
            _writer.CompleteRowGroup(rowCount);
        return ValueTask.CompletedTask;
    }

    static EncodingKind ResolveDefaultEncoding(ImmutableArray<EncodingKind> encodings)
        => encodings.IsDefaultOrEmpty ? EncodingKind.Plain : encodings[0];


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
