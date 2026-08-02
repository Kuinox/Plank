using Apache.Arrow;
using Plank.Writing;

namespace Plank.Arrow;

/// <summary>Writes Apache Arrow record batches and tables as Parquet row groups with Plank.</summary>
public sealed class ArrowParquetWriter : IDisposable
{
    readonly Stream _destination;
    readonly bool _leaveOpen;
    readonly ParquetWriter _writer;
    readonly ArrowColumnWriter[] _columns;
    bool _closed;
    bool _faulted;

    /// <summary>Creates a writer for a fixed flat Arrow schema.</summary>
    /// <param name="destination">The writable destination stream.</param>
    /// <param name="schema">The Arrow schema used by every record batch or table.</param>
    /// <param name="options">Optional Plank writer options.</param>
    /// <param name="leaveOpen">Whether closing this writer leaves <paramref name="destination"/> open.</param>
    public ArrowParquetWriter(Stream destination, Apache.Arrow.Schema schema,
        ParquetWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(schema);

        _destination = destination;
        _leaveOpen = leaveOpen;
        Schema = schema;
        var parquetSchema = ArrowSchemaConverter.ToParquetSchema(schema);
        _writer = parquetSchema.CreateWriter(leaveOpen ? new NonDisposingStream(destination) : destination, options);
        _columns = new ArrowColumnWriter[parquetSchema.LeafColumns.Length];
        for (var i = 0; i < _columns.Length; i++)
            _columns[i] = new ArrowColumnWriter(_writer, parquetSchema.LeafColumns[i], schema.GetFieldByIndex(i));
    }

    /// <summary>Gets the Arrow schema accepted by this writer.</summary>
    public Apache.Arrow.Schema Schema { get; }

    /// <summary>Writes one Arrow record batch as one Parquet row group.</summary>
    public void WriteRecordBatch(RecordBatch recordBatch)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(recordBatch);
        ArrowSchemaConverter.EnsureEquivalent(Schema, recordBatch.Schema);
        if (recordBatch.ColumnCount != _columns.Length)
            throw new ArgumentException(
                $"Arrow record batch has {recordBatch.ColumnCount} arrays; expected {_columns.Length}.",
                nameof(recordBatch));
        ValidateEmptySchemaRowCount(recordBatch.Length);

        try
        {
            var rowGroup = _writer.StartRowGroup();
            for (var i = 0; i < _columns.Length; i++)
                _columns[i].Write(rowGroup, recordBatch.Column(i), recordBatch.Length);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Writes an Arrow table as one Parquet row group, joining chunks within each table column.</summary>
    /// <remarks>The table must contain at most <see cref="int.MaxValue"/> rows.</remarks>
    public void WriteTable(Table table)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(table);
        ArrowSchemaConverter.EnsureEquivalent(Schema, table.Schema);
        if (table.ColumnCount != _columns.Length)
            throw new ArgumentException($"Arrow table has {table.ColumnCount} columns; expected {_columns.Length}.",
                nameof(table));
        var rowCount = checked((int)table.RowCount);
        ValidateEmptySchemaRowCount(rowCount);

        try
        {
            var rowGroup = _writer.StartRowGroup();
            for (var i = 0; i < _columns.Length; i++)
                _columns[i].Write(rowGroup, table.Column(i).Data, rowCount);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Finishes the Parquet footer and closes the writer.</summary>
    public void Close()
    {
        if (_closed)
            return;
        if (_faulted)
            throw new InvalidOperationException("Cannot close an Arrow Parquet writer after a row-group write failed.");

        _writer.CloseFile();
        _closed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_closed)
            return;
        if (_faulted)
        {
            _closed = true;
            if (!_leaveOpen)
                _destination.Dispose();
            return;
        }

        Close();
    }

    void ValidateEmptySchemaRowCount(int rowCount)
    {
        if (_columns.Length == 0 && rowCount != 0)
            throw new NotSupportedException("Parquet cannot preserve rows from an Arrow batch with no fields.");
    }

    void ThrowIfUnavailable()
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(ArrowParquetWriter));
        if (_faulted)
            throw new InvalidOperationException("The Arrow Parquet writer cannot be reused after a row-group write failed.");
    }
}
