namespace Plank;

public sealed class RowGroupWriter : IDisposable
{
    readonly ParquetWriter _writer;
    readonly RowGroupOptions _options;
    readonly bool[] _staged;
    readonly EncodingKind[] _stagedEncoding;
    readonly CompressionKind[] _stagedCompression;
    readonly int[] _stagedValueCount;
    int _nextOrdinal;

    internal RowGroupWriter(ParquetWriter writer, int rowCount, RowGroupOptions options)
    {
        _writer = writer;
        _options = options;
        RowCount = rowCount;

        var columnCount = writer.Schema.Columns.Count;
        _staged = new bool[columnCount];
        _stagedEncoding = new EncodingKind[columnCount];
        _stagedCompression = new CompressionKind[columnCount];
        _stagedValueCount = new int[columnCount];
        _nextOrdinal = 0;
    }

    public int RowCount { get; }

    public ColumnWriter<T> Column<T>(ParquetSchema.Column<T> column)
    {
        if (column is null)
            throw new ArgumentNullException(nameof(column));

        if (column.Ordinal < 0 || column.Ordinal >= _staged.Length)
            throw new ArgumentOutOfRangeException(nameof(column), $"Column '{column.Name}' ordinal is out of range.");

        return new ColumnWriter<T>(this, column);
    }

    internal SerializedColumn Serialize<T>(ParquetSchema.Column<T> column, ReadOnlySpan<T> values, EncodingKind? encoding, CompressionKind? compression)
    {
        var ordinal = column.Ordinal;
        if (_staged[ordinal])
            throw new InvalidOperationException($"Column '{column.Name}' is already serialized.");

        _staged[ordinal] = true;
        _stagedValueCount[ordinal] = values.Length;
        _stagedEncoding[ordinal] = encoding ?? ResolveDefaultEncoding(column);
        _stagedCompression[ordinal] = compression ?? CompressionKind.None;

        return new SerializedColumn(this, ordinal);
    }

    internal ValueTask WriteSerializedAsync(int ordinal, CancellationToken cancellationToken)
    {
        if (ordinal < 0 || ordinal >= _staged.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        if (!_staged[ordinal])
            throw new InvalidOperationException($"Column ordinal {ordinal} has no serialized payload.");

        if (ordinal != _nextOrdinal)
            throw new InvalidOperationException($"Column ordinal {ordinal} was written out of order. Expected {_nextOrdinal}.");

        _staged[ordinal] = false;
        _stagedValueCount[ordinal] = 0;
        _nextOrdinal++;
        return ValueTask.CompletedTask;
    }

    static EncodingKind ResolveDefaultEncoding(ParquetSchema.Column column)
    {
        var encodings = column.Encodings;
        return encodings.Length == 0 ? EncodingKind.Plain : encodings[0];
    }

    public void Dispose()
    {
    }
}
