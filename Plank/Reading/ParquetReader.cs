using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Reading;

public sealed class ParquetReader : IDisposable
{
    static ReadOnlySpan<byte> FileMagic
        => "PAR1"u8;

    readonly ParquetSchema _schema;
    readonly ParquetReaderOptions _options;
    InternalParquetFooter _footer;
    ParquetFileMetadata _metadata;
    byte[] _footerBuffer;
    StreamReadSource? _streamSource;
    bool _disposed;

    internal ParquetReader(Stream stream, ParquetSchema schema, ParquetReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _schema = schema;
        _options = options;
        _footer = InternalParquetFooter.Empty;
        _metadata = default;
        _footerBuffer = [];
        _streamSource = new StreamReadSource(stream);
        _disposed = false;
        Reset(_streamSource);
    }

    internal ParquetReader(IParquetReadSource source, ParquetSchema schema, ParquetReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _schema = schema;
        _options = options;
        _footer = InternalParquetFooter.Empty;
        _metadata = default;
        _footerBuffer = [];
        _streamSource = null;
        _disposed = false;
        Reset(source);
    }

    public ParquetSchema Schema
        => _schema;

    public ParquetFileMetadata Metadata
        => _metadata;

    public ParquetFooter Footer
        => new(this);

    internal ParquetReaderOptions Options
        => _options;

    public void Reset(Stream stream)
    {
        ThrowIfDisposed();
        if (_streamSource is null)
            _streamSource = new StreamReadSource(stream);
        else
            _streamSource.Reset(stream);
        Reset(_streamSource);
    }

    public void Reset(IParquetReadSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 12)
            throw new CorruptParquetException("Stream is too small to contain a Parquet footer.");

        Span<byte> trailer = stackalloc byte[8];
        source.ReadExactly(source.Length - (ulong)trailer.Length, trailer);
        if (!trailer[4..].SequenceEqual(FileMagic))
            throw new CorruptParquetException("Stream does not end with the PAR1 footer marker.");

        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(trailer[..4]);
        if (footerLength < 0)
            throw new CorruptParquetException("Footer length must be non-negative.");
        if ((ulong)footerLength > source.Length - (ulong)trailer.Length)
            throw new CorruptParquetException("Footer length exceeds stream size.");

        var footerOffset = source.Length - (ulong)trailer.Length - (ulong)footerLength;
        if (footerOffset < 4)
            throw new CorruptParquetException("Footer offset is invalid for this stream.");

        if (_footerBuffer.Length < footerLength)
            _footerBuffer = new byte[footerLength];
        var footerBytes = _footerBuffer.AsSpan(0, footerLength);
        source.ReadExactly(footerOffset, footerBytes);

        _footer = ParquetMetadataThriftReader.Read(footerBytes, footerOffset, _footer);
        _metadata = new ParquetFileMetadata(_schema, (long)footerOffset, footerLength, _footer.Version);
    }

    public RowGroupTokenEnumerable EnumerateRowGroups()
    {
        ThrowIfDisposed();
        return new RowGroupTokenEnumerable(this);
    }

    public RowGroupReader OpenRowGroup(Stream stream, RowGroupToken token)
        => OpenRowGroup(new StreamReadSource(stream), token);

    public RowGroupReader OpenRowGroup(IParquetReadSource source, RowGroupToken token)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        var rowGroup = GetRowGroup(token);
        return new RowGroupReader(this, source, rowGroup);
    }

    internal RowGroupReadContext CreateRowGroupReadContext()
        => new(_schema.Columns.Length);

    public RowGroupReader CreateReusableRowGroupReader()
        => new(CreateRowGroupReadContext());

    public RowGroupReader OpenRowGroup(IParquetReadSource source, RowGroupToken token, RowGroupReader reusable)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reusable);

        var rowGroup = GetRowGroup(token);
        reusable.Reset(this, source, rowGroup);
        return reusable;
    }

    InternalRowGroupMetadata GetRowGroup(RowGroupToken token)
    {
        if (token.RowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(token), token.RowGroupOrdinal, "Row group ordinal must be non-negative.");
        if ((uint)token.RowGroupOrdinal >= (uint)_footer.RowGroups.Length)
            throw new ArgumentOutOfRangeException(nameof(token), token.RowGroupOrdinal, "Row group ordinal is outside the parsed footer.");

        var rowGroup = _footer.RowGroups[token.RowGroupOrdinal];
        if (rowGroup.MetadataOffset != token.MetadataOffset || rowGroup.ColumnChunkOffset != token.ColumnChunkOffset)
            throw new ArgumentException("Row group token does not belong to this reader.", nameof(token));

        return rowGroup;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetReader));
    }

    internal bool TryReadNextRowGroupToken(ref long cursor, out RowGroupToken token)
    {
        ThrowIfDisposed();
        if (cursor < 0)
            cursor = 0;
        if (cursor >= _footer.RowGroups.Length)
        {
            token = default;
            return false;
        }

        var rowGroup = _footer.RowGroups[(int)cursor];
        token = new RowGroupToken((int)cursor, rowGroup.MetadataOffset, rowGroup.ColumnChunkOffset);
        cursor++;
        return true;
    }

    internal int GetColumnOrdinal(Column column)
    {
        var ordinal = _schema.Columns.IndexOf(column);
        if (ordinal >= 0)
            return ordinal;

        throw new ArgumentException("Column does not belong to this schema.", nameof(column));
    }

    internal InternalColumnChunkMetadata GetColumnChunk(int rowGroupOrdinal, int columnOrdinal)
        => _footer.RowGroups[rowGroupOrdinal].Columns[columnOrdinal];
}
