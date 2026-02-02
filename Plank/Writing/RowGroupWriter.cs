using Plank.Schema;

namespace Plank.Writing;

public readonly struct RowGroupWriter : IDisposable, IEquatable<RowGroupWriter>
{
    readonly State _state;

    internal RowGroupWriter(ParquetWriter writer, int rowCount, RowGroupOptions options)
    {
        var columnCount = writer.Schema.Columns.Count;
        _state = new State(writer, options, rowCount, columnCount);
    }

    public int RowCount
        => _state.RowCount;

    public ColumnWriter Column(ParquetSchema.Column column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (column.Ordinal < 0 || column.Ordinal >= _state.Staged.Length)
            throw new ArgumentOutOfRangeException(nameof(column), $"Column '{column.Name}' ordinal is out of range.");

        return new ColumnWriter(this, column);
    }

    internal SerializedColumn Serialize<T>(ParquetSchema.Column column, ReadOnlySpan<T> values, EncodingKind? encoding, CompressionKind? compression)
    {
        if (column.ClrType != typeof(T))
            throw new InvalidOperationException($"Column '{column.Name}' expects {column.ClrType}, but received {typeof(T)}.");

        var ordinal = column.Ordinal;
        if (_state.Staged[ordinal])
            throw new InvalidOperationException($"Column '{column.Name}' is already serialized.");

        _state.Staged[ordinal] = true;
        _state.StagedValueCount[ordinal] = values.Length;
        _state.StagedEncoding[ordinal] = encoding ?? ResolveDefaultEncoding(column);
        _state.StagedCompression[ordinal] = compression ?? CompressionKind.None;

        return new SerializedColumn(this, ordinal);
    }

    internal ValueTask WriteSerializedAsync(int ordinal, CancellationToken cancellationToken)
    {
        if (ordinal < 0 || ordinal >= _state.Staged.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        if (!_state.Staged[ordinal])
            throw new InvalidOperationException($"Column ordinal {ordinal} has no serialized payload.");

        if (ordinal != _state.NextOrdinal)
            throw new InvalidOperationException($"Column ordinal {ordinal} was written out of order. Expected {_state.NextOrdinal}.");

        _state.Staged[ordinal] = false;
        _state.StagedValueCount[ordinal] = 0;
        _state.NextOrdinal++;
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
        internal readonly bool[] Staged;
        internal readonly EncodingKind[] StagedEncoding;
        internal readonly CompressionKind[] StagedCompression;
        internal readonly int[] StagedValueCount;
        internal readonly int RowCount;
        internal int NextOrdinal;

        internal State(ParquetWriter writer, RowGroupOptions options, int rowCount, int columnCount)
        {
            Writer = writer;
            Options = options;
            RowCount = rowCount;
            Staged = new bool[columnCount];
            StagedEncoding = new EncodingKind[columnCount];
            StagedCompression = new CompressionKind[columnCount];
            StagedValueCount = new int[columnCount];
            NextOrdinal = 0;
        }
    }
}
