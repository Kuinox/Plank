using System.Buffers.Binary;
using Plank.Reading.Physical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading.Physical;

public sealed class ParquetFileReader : IDisposable
{
    static ReadOnlySpan<byte> FileMagic
        => "PAR1"u8;

    readonly ParquetFileReaderOptions _options;
    readonly ParquetFileMetadata _metadata = new();
    IParquetReadSource? _source;
    int _generation;
    bool _disposed;

    public ParquetFileReader(ParquetFileReaderOptions? options = null)
    {
        _options = options ?? ParquetFileReaderOptions.Default;
        _options.Validate();
    }

    public ParquetFileMetadata Metadata
    {
        get
        {
            ThrowIfDisposed();
            return _metadata;
        }
    }

    /// <summary>
    /// Reads the parquet footer from <paramref name="stream"/> and makes it the current source for page reads.
    /// </summary>
    /// <param name="stream">The stream containing a parquet file.</param>
    /// <remarks>
    /// Resetting the reader invalidates page cursors created from an earlier source. After the first stream reset,
    /// the reader keeps the same stream wrapper. Metadata buffers come from the configured pool, so reset does not
    /// allocate them when the pool already has arrays big enough.
    /// </remarks>
    public void Reset(Stream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        if (_source is not StreamReadSource source)
            source = new StreamReadSource(stream);
        else
            source.Reset(stream);

        ResetCore(source);
        _source = source;
    }

    /// <summary>
    /// Reads the parquet footer from <paramref name="source"/> and makes it the current source for page reads.
    /// </summary>
    /// <param name="source">The random-access parquet source to read from.</param>
    /// <remarks>
    /// Resetting the reader invalidates page cursors created from an earlier source. Metadata buffers come from the
    /// configured pool, so reset does not allocate them when the pool already has arrays big enough.
    /// </remarks>
    public void Reset(IParquetReadSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ResetCore(source);
        _source = source;
    }

    public ParquetPageCursor OpenPages(int rowGroupOrdinal, int columnOrdinal)
    {
        ValidateGeneration(_generation);
        ValidateOrdinal(rowGroupOrdinal, _metadata.RowGroupCount, nameof(rowGroupOrdinal));
        return new ParquetPageCursor(this, _generation, rowGroupOrdinal, columnOrdinal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _generation++;
        ReturnMetadataBuffers();
        _source = null;
    }

    void ResetCore(IParquetReadSource source)
    {
        _generation++;
        _source = null;
        ReturnMetadataBuffers();

        try
        {
            if (source.Length < 12)
                throw new CorruptParquetException("Stream is too small to contain a Parquet footer.");

            Span<byte> trailer = stackalloc byte[8];
            source.ReadExactly(source.Length - (ulong)trailer.Length, trailer);
            if (!trailer[4..].SequenceEqual(FileMagic))
                throw new CorruptParquetException("Stream does not end with the PAR1 footer marker.");

            var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
            if (footerLength > source.Length - (ulong)trailer.Length)
                throw new CorruptParquetException("Footer length exceeds stream size.");

            var footerOffset = source.Length - (ulong)trailer.Length - footerLength;
            if (footerOffset < 4)
                throw new CorruptParquetException("Footer offset is invalid for this stream.");

            _metadata.FooterOffset = footerOffset;
            _metadata.FooterLength = footerLength;
            _metadata.FooterBuffer = Rent<byte>(footerLength);
            _metadata.FooterByteCount = checked((int)footerLength);
            var footerBytes = _metadata.FooterBuffer.AsSpan(0, _metadata.FooterByteCount);
            source.ReadExactly(footerOffset, footerBytes);
            PhysicalMetadataThriftReader.Read(_metadata, _options.BufferPool);
        }
        catch
        {
            ReturnMetadataBuffers();
            throw;
        }
    }

    internal IParquetBufferPool BufferPool
        => _options.BufferPool;

    internal IParquetReadSource Source
    {
        get
        {
            ThrowIfDisposed();
            return _source ?? throw new InvalidOperationException("The reader has not been reset with a source.");
        }
    }

    T[] Rent<T>(uint count)
        => count == 0 ? [] : _options.BufferPool.Rent<T>(count);

    void ReturnMetadataBuffers()
        => _metadata.ReturnBuffers(_options.BufferPool);

    internal void ValidateGeneration(int generation)
    {
        ThrowIfDisposed();
        if (generation != _generation)
            throw new InvalidOperationException("The reader handle is stale because the reader was reset.");
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetFileReader));
    }

    static void ValidateOrdinal(int ordinal, int count, string parameterName)
    {
        if ((uint)ordinal >= (uint)count)
            throw new ArgumentOutOfRangeException(parameterName, ordinal,
                $"Ordinal must be between zero and {count - 1}.");
    }
}
