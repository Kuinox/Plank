using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Writing;

public sealed class ParquetWriter
{
    Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    internal readonly Column[] ColumnsByOrdinal;
    internal readonly int ColumnCount;
    internal readonly BufferWriterFactory BufferWriters;
    internal readonly CompressionKind Compression;
    internal BufferWriter SerializedRowGroupsMetadata;
    int _rowGroupCount;
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
        _options.Validate();
        ColumnsByOrdinal = _schema.Columns.IsDefault ? [] : _schema.Columns.ToArray();
        ColumnCount = ColumnsByOrdinal.Length;
        BufferWriters = new BufferWriterFactory(_options.BufferPool, _options.BufferChunkSizeBytes,
            _options.InitialPageBufferBytes, _options.InitialColumnBufferBytes, _options.BufferChunkSizeBytes);
        Compression = _options.Compression;
        SerializedRowGroupsMetadata = BufferWriters.CreateMetadataBufferWriter();
        _rowGroupCount = 0;
        _rowGroupOpen = false;
        _streamDisposed = false;
        _schema.Validate();
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema,
        ParquetWriterOptions? options = null)
        => new(stream, schema, options ?? ParquetWriterOptions.Default);

    public SerializedColumn CreateSerializedColumn()
        => new(this, _options.InitialPageCapacity);

    public void Reset(Stream stream)
    {
        ThrowIfStreamClosed();
        ArgumentNullException.ThrowIfNull(stream);
        if (_rowGroupOpen)
            throw new InvalidOperationException("Cannot reset while a row group is open.");

        DisposeCurrentStream();
        _stream = stream;
        _streamDisposed = false;
        SerializedRowGroupsMetadata.Reset();
        _rowGroupCount = 0;
    }

    public RowGroupWriter StartRowGroup()
    {
        ThrowIfStreamClosed();
        if (_rowGroupOpen)
            throw new InvalidOperationException("A row group is already open for this writer.");

        _rowGroupOpen = true;
        if (ColumnCount == 0)
        {
            const int size = sizeof(int) + sizeof(int);
            var span = SerializedRowGroupsMetadata.GetSpan(size);
            BinaryPrimitives.WriteInt32LittleEndian(span[0..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], 0);
            SerializedRowGroupsMetadata.Advance(size);
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
        var ordinal = Array.IndexOf(ColumnsByOrdinal, column);
        if (ordinal >= 0)
            return ordinal;

        throw new ArgumentException("SerializedColumn column does not belong to this schema.", nameof(column));
    }

    internal void WriteBuffer(ref BufferWriter buffer)
        => buffer.WriteTo(_stream);

    internal void CompleteOpenRowGroup()
    {
        _rowGroupCount++;
        _rowGroupOpen = false;
    }
}
