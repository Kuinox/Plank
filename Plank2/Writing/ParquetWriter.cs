using System.Buffers.Binary;
using Plank.Schema;

namespace Plank2.Writing;

public sealed class ParquetWriter
{
    Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly Column[] _columnsByOrdinal;
    BufferWriter _serializedRowGroupsMetadata;
    int _rowGroupCount;
    int _openRowGroupMetadataColumnsWritten;
    bool _openRowGroupMetadataStarted;
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
        _serializedRowGroupsMetadata = default;
        _rowGroupCount = 0;
        _openRowGroupMetadataColumnsWritten = 0;
        _openRowGroupMetadataStarted = false;
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
        _serializedRowGroupsMetadata.Reset();
        _rowGroupCount = 0;
        _openRowGroupMetadataColumnsWritten = 0;
        _openRowGroupMetadataStarted = false;
    }

    public RowGroupWriter StartRowGroup()
    {
        ThrowIfStreamClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("A row group is already open for this writer.");

        _rowGroupOpen = true;
        _openRowGroupMetadataColumnsWritten = 0;
        _openRowGroupMetadataStarted = false;
        if (_columnsByOrdinal.Length == 0)
        {
            BeginOpenRowGroupMetadata(0);
            CompleteOpenRowGroup();
        }

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

    internal void WriteBuffer(BufferWriter buffer)
    {
        ThrowIfStreamClosed();
        _stream.Write(buffer.WrittenSpan);
    }

    internal void EnsureRowGroupOpen()
    {
        if (!_rowGroupOpen)
            throw new InvalidOperationException("No row group is currently open.");
    }

    internal void BeginOpenRowGroupMetadata(int rowCount)
    {
        EnsureRowGroupOpen();
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be non-negative.");
        if (_openRowGroupMetadataStarted)
            throw new InvalidOperationException("Row group metadata was already started.");

        WriteInt32(ref _serializedRowGroupsMetadata, rowCount);
        WriteInt32(ref _serializedRowGroupsMetadata, _columnsByOrdinal.Length);
        _openRowGroupMetadataStarted = true;
        _openRowGroupMetadataColumnsWritten = 0;
    }

    internal void AppendOpenRowGroupColumnMetadata(int rowCount, int valueCount, long totalUncompressedSize,
        long totalCompressedSize)
    {
        EnsureRowGroupOpen();
        if (!_openRowGroupMetadataStarted)
            throw new InvalidOperationException("Row group metadata is not started.");

        WriteInt32(ref _serializedRowGroupsMetadata, rowCount);
        WriteInt32(ref _serializedRowGroupsMetadata, valueCount);
        WriteInt64(ref _serializedRowGroupsMetadata, totalUncompressedSize);
        WriteInt64(ref _serializedRowGroupsMetadata, totalCompressedSize);
        _openRowGroupMetadataColumnsWritten++;
    }

    internal void CompleteOpenRowGroup()
    {
        EnsureRowGroupOpen();
        if (!_openRowGroupMetadataStarted)
            throw new InvalidOperationException("Row group metadata is not started.");
        if (_openRowGroupMetadataColumnsWritten != _columnsByOrdinal.Length)
            throw new InvalidOperationException(
                $"Row group metadata is incomplete. Expected {_columnsByOrdinal.Length} columns, got {_openRowGroupMetadataColumnsWritten}.");

        _rowGroupCount++;
        _openRowGroupMetadataStarted = false;
        _openRowGroupMetadataColumnsWritten = 0;
        _rowGroupOpen = false;
    }

    static void WriteInt32(ref BufferWriter writer, int value)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
        writer.Advance(sizeof(int));
    }

    static void WriteInt64(ref BufferWriter writer, long value)
    {
        var destination = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
        writer.Advance(sizeof(long));
    }
}
