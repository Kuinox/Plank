using Plank.Schema;

namespace Plank.Writing;

public readonly struct RowGroupWriter : IDisposable, IEquatable<RowGroupWriter>
{
    const int StagedFree = 0;
    const int StagedWriting = 1;
    const int StagedReady = 2;

    readonly State _state;

    internal RowGroupWriter(ParquetWriter writer, RowGroupOptions options)
    {
        var columnCount = writer.Schema.Columns.Count;
        _state = new State(writer, options, columnCount);
    }

    public int RowCount
        => Volatile.Read(ref _state.RowCount);

    public SerializedColumn Serialize<T>(ParquetSchema.Column column, ReadOnlySpan<T> values)
        => Serialize(column, values, null, null);

    public ValueTask WriteAsync<T>(ParquetSchema.Column column, ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
        => Serialize(column, values).WriteAsync(cancellationToken);

    internal SerializedColumn Serialize<T>(ParquetSchema.Column column, ReadOnlySpan<T> values, EncodingKind? encoding, CompressionKind? compression)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (column.Ordinal < 0 || column.Ordinal >= _state.Staged.Length)
            throw new ArgumentOutOfRangeException(nameof(column), $"Column '{column.Name}' ordinal is out of range.");

        if (column.ClrType != typeof(T))
            throw new InvalidOperationException($"Column '{column.Name}' expects {column.ClrType}, but received {typeof(T)}.");

        var ordinal = column.Ordinal;
        if (Interlocked.CompareExchange(ref _state.Staged[ordinal], StagedWriting, StagedFree) != StagedFree)
            throw new InvalidOperationException($"Column '{column.Name}' is already serialized.");

        _state.StagedValueCount[ordinal] = values.Length;
        _state.StagedEncoding[ordinal] = encoding ?? ResolveDefaultEncoding(column);
        _state.StagedCompression[ordinal] = compression ?? CompressionKind.None;
        Volatile.Write(ref _state.Staged[ordinal], StagedReady);

        return new SerializedColumn(this, ordinal);
    }

    internal ValueTask WriteSerializedAsync(int ordinal, CancellationToken cancellationToken)
    {
        if (ordinal < 0 || ordinal >= _state.Staged.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        if (_state.Staged[ordinal] != StagedReady)
            throw new InvalidOperationException($"Column ordinal {ordinal} has no serialized payload.");

        var valueCount = _state.StagedValueCount[ordinal];
        var rowCount = Volatile.Read(ref _state.RowCount);
        if (rowCount < 0)
        {
            var previous = Interlocked.CompareExchange(ref _state.RowCount, valueCount, -1);
            rowCount = previous < 0 ? valueCount : previous;
        }

        if (rowCount != valueCount)
            throw new InvalidOperationException($"Column ordinal {ordinal} has {valueCount} values but row group expects {rowCount}.");

        var expected = ordinal;
        if (Interlocked.CompareExchange(ref _state.NextOrdinal, expected + 1, expected) != expected)
            throw new InvalidOperationException($"Column ordinal {ordinal} was written out of order. Expected {_state.NextOrdinal}.");

        Volatile.Write(ref _state.Staged[ordinal], StagedFree);
        _state.StagedValueCount[ordinal] = 0;
        return ValueTask.CompletedTask;
    }

    static EncodingKind ResolveDefaultEncoding(ParquetSchema.Column column)
    {
        var encodings = column.Encodings;
        return encodings.IsEmpty ? EncodingKind.Plain : encodings[0];
    }

    public bool Equals(RowGroupWriter other)
        => ReferenceEquals(_state, other._state);

    public override bool Equals(object? obj)
        => obj is RowGroupWriter other && Equals(other);

    public override int GetHashCode()
        => _state.GetHashCode();

    public static bool operator ==(RowGroupWriter left, RowGroupWriter right)
        => left.Equals(right);

    public static bool operator !=(RowGroupWriter left, RowGroupWriter right)
        => !left.Equals(right);

    public void Dispose()
    {
    }

    sealed class State
    {
        internal readonly ParquetWriter Writer;
        internal readonly RowGroupOptions Options;
        internal readonly int[] Staged;
        internal readonly EncodingKind[] StagedEncoding;
        internal readonly CompressionKind[] StagedCompression;
        internal readonly int[] StagedValueCount;
        internal int RowCount;
        internal int NextOrdinal;

        internal State(ParquetWriter writer, RowGroupOptions options, int columnCount)
        {
            Writer = writer;
            Options = options;
            RowCount = -1;
            Staged = new int[columnCount];
            StagedEncoding = new EncodingKind[columnCount];
            StagedCompression = new CompressionKind[columnCount];
            StagedValueCount = new int[columnCount];
            NextOrdinal = 0;
        }
    }
}
