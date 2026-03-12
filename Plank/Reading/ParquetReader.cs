using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Reading;

public sealed class ParquetReader : IDisposable
{
    static ReadOnlySpan<byte> FileMagic
        => "PAR1"u8;

    readonly ParquetSchema _schema;
    InternalParquetFooter _footer;
    ParquetFileMetadata _metadata;
    byte[] _footerBuffer;
    bool _disposed;

    internal ParquetReader(Stream stream, ParquetSchema schema, ParquetReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);

        _schema = schema;
        _footer = InternalParquetFooter.Empty;
        _metadata = default;
        _footerBuffer = [];
        _disposed = false;
        Reset(stream);
    }

    public ParquetSchema Schema
        => _schema;

    public ParquetFileMetadata Metadata
        => _metadata;

    public ParquetFooter Footer
        => new(this);

    public void Reset(Stream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new InvalidOperationException("Reader stream must be readable.");
        if (!stream.CanSeek)
            throw new InvalidOperationException("Reader stream must be seekable.");
        if (stream.Length < 12)
            throw new InvalidDataException("Stream is too small to contain a Parquet footer.");

        Span<byte> trailer = stackalloc byte[8];
        stream.Position = stream.Length - trailer.Length;
        stream.ReadExactly(trailer);
        if (!trailer[4..].SequenceEqual(FileMagic))
            throw new InvalidDataException("Stream does not end with the PAR1 footer marker.");

        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(trailer[..4]);
        if (footerLength < 0)
            throw new InvalidDataException("Footer length must be non-negative.");

        var footerOffset = stream.Length - trailer.Length - footerLength;
        if (footerOffset < 4)
            throw new InvalidDataException("Footer offset is invalid for this stream.");

        if (_footerBuffer.Length < footerLength)
            _footerBuffer = new byte[footerLength];
        var footerBytes = _footerBuffer.AsSpan(0, footerLength);
        stream.Position = footerOffset;
        stream.ReadExactly(footerBytes);

        _footer = ParquetMetadataThriftReader.Read(footerBytes, footerOffset);
        _metadata = new ParquetFileMetadata(_schema, footerOffset, footerLength, _footer.Version);
    }

    public RowGroupTokenEnumerable EnumerateRowGroups()
    {
        ThrowIfDisposed();
        return new RowGroupTokenEnumerable(this);
    }

    public RowGroupReader OpenRowGroup(Stream stream, RowGroupToken token)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new InvalidOperationException("Row group stream must be readable.");
        if (!stream.CanSeek)
            throw new InvalidOperationException("Row group stream must be seekable.");

        if (token.RowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(token), token.RowGroupOrdinal, "Row group ordinal must be non-negative.");
        if ((uint)token.RowGroupOrdinal >= (uint)_footer.RowGroups.Length)
            throw new ArgumentOutOfRangeException(nameof(token), token.RowGroupOrdinal, "Row group ordinal is outside the parsed footer.");

        var rowGroup = _footer.RowGroups[token.RowGroupOrdinal];
        if (rowGroup.MetadataOffset != token.MetadataOffset || rowGroup.ColumnChunkOffset != token.ColumnChunkOffset)
            throw new ArgumentException("Row group token does not belong to this reader.", nameof(token));

        return new RowGroupReader(this, stream, rowGroup);
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
