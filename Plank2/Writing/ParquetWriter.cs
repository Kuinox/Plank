using Plank.Schema;

namespace Plank2.Writing;

public sealed class ParquetWriter
{
    Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly Column[] _columnsByOrdinal;
    bool _rowGroupOpen;
    bool _streamDisposed;

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);

        _stream = stream;
        _schema = schema;
        _options = options;
        _columnsByOrdinal = _schema.Columns.IsDefault ? [] : _schema.Columns.ToArray();
        _rowGroupOpen = false;
        _streamDisposed = false;
        _schema.Validate();
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema,
        ParquetWriterOptions? options = null)
        => new(stream, schema, options ?? ParquetWriterOptions.Default);

    public SerializedColumn CreateSerializedColumn()
        => new(this, _options.InitialPageCapacity, _options.Log);

    public void Reset(Stream stream)
    {
        ThrowIfStreamClosed();
        ArgumentNullException.ThrowIfNull(stream);
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot reset while a row group is open.");

        DisposeCurrentStream();
        _stream = stream;
        _streamDisposed = false;
    }

    public RowGroupWriter StartRowGroup()
    {
        ThrowIfStreamClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("A row group is already open for this writer.");

        _rowGroupOpen = true;
        if (_columnsByOrdinal.Length == 0)
            CompleteOpenRowGroup();
        return new RowGroupWriter(this);
    }

    public void CloseFile()
    {
        ThrowIfStreamClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot close the file while a row group is still open.");

        DisposeCurrentStream();
    }

    void ThrowIfStreamClosed()
    {
        if (_streamDisposed)
            throw new InvalidOperationException("The current file stream is closed. Call Reset(stream) to start a new file.");
    }

    void DisposeCurrentStream()
    {
        if (_streamDisposed)
            return;
        _stream.Dispose();
        _streamDisposed = true;
    }

    internal void WriteSerializedColumnToOpenRowGroup(SerializedColumn serialized)
    {
        if (!_rowGroupOpen)
            throw new InvalidOperationException("No row group is currently open.");
        ArgumentNullException.ThrowIfNull(serialized);
        serialized.Consume(this);
    }

    internal int GetColumnOrdinal(Column column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var ordinal = Array.IndexOf(_columnsByOrdinal, column);
        if (ordinal >= 0)
            return ordinal;

        throw new ArgumentException("SerializedColumn column does not belong to this schema.", nameof(column));
    }

    internal int ColumnCount
        => _columnsByOrdinal.Length;

    internal void CompleteOpenRowGroup()
        => _rowGroupOpen = false;
}
