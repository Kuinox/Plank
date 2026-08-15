using System.Buffers.Binary;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical.Internal;
using Plank.Schema;

namespace Plank.Reading.Physical;

public sealed class ParquetFileReader : IDisposable
{
    static ReadOnlySpan<byte> FileMagic
        => "PAR1"u8;

    readonly ParquetFileReaderOptions _options;
    readonly ParquetFileMetadata _metadata = new();
    IParquetReadSource? _source;
    // The wrapper we built around a caller's Stream, and that stream. We close a
    // stream we wrapped ourselves; a caller-supplied IParquetReadSource stays
    // the caller's to manage, since we did not open anything on their behalf.
    StreamReadSource? _ownedSource;
    Stream? _ownedStream;
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
    /// <para>
    /// The reader takes ownership of <paramref name="stream"/>: it is closed when the reader is reset onto a
    /// different source, and when the reader is disposed. Pass a <see cref="IParquetReadSource"/> instead to keep
    /// ownership of the underlying resource.
    /// </para>
    /// <para>
    /// Resetting the reader invalidates page cursors created from an earlier source. After the first stream reset,
    /// the reader keeps the same stream wrapper. Metadata buffers come from the configured pool, so reset does not
    /// allocate them when the pool already has arrays big enough.
    /// </para>
    /// </remarks>
    public void Reset(Stream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);

        // Retargeting abandons the previous stream, so close it here: callers
        // reset across a run of files and only dispose the reader at the end,
        // which otherwise left every earlier file handle open until finalization.
        if (!ReferenceEquals(_ownedStream, stream))
            DisposeOwnedStream();

        if (_ownedSource is null)
            _ownedSource = new StreamReadSource(stream);
        else
            _ownedSource.Reset(stream);

        ResetCore(_ownedSource);
        _source = _ownedSource;
        _ownedStream = stream;
    }

    /// <summary>
    /// Reads the parquet footer from <paramref name="source"/> and makes it the current source for page reads.
    /// </summary>
    /// <param name="source">The random-access parquet source to read from.</param>
    /// <remarks>
    /// <para>
    /// The caller keeps ownership of <paramref name="source"/>: the reader never disposes it, so a source holding a
    /// handle (such as <see cref="FileReadSource"/>) must be disposed by whoever constructed it. This is what lets a
    /// single source be shared — an in-place merge reads and writes through one
    /// <see cref="Plank.Writing.IParquetReadWriteSource"/>, and would break if resetting the reader closed it.
    /// Any stream the reader had wrapped itself is still closed here, since resetting abandons it.
    /// </para>
    /// <para>
    /// Resetting the reader invalidates page cursors created from an earlier source. Metadata buffers come from the
    /// configured pool, so reset does not allocate them when the pool already has arrays big enough.
    /// </para>
    /// </remarks>
    public void Reset(IParquetReadSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        // Switching to a caller-owned source still abandons any stream we had
        // wrapped, so that one is ours to close.
        DisposeOwnedStream();
        ResetCore(source);
        _source = source;
    }

    public ParquetPageCursor OpenPages(int rowGroupOrdinal, int columnOrdinal)
    {
        ValidateGeneration(_generation);
        ValidateOrdinal(rowGroupOrdinal, _metadata.RowGroupCount, nameof(rowGroupOrdinal));
        return new ParquetPageCursor(this, _generation, rowGroupOrdinal, columnOrdinal);
    }

    /// <summary>Loads a column chunk's split-block Bloom filter.</summary>
    public ParquetBloomFilter OpenBloomFilter(int rowGroupOrdinal, int columnOrdinal)
    {
        ValidateGeneration(_generation);
        ValidateOrdinal(rowGroupOrdinal, _metadata.RowGroupCount, nameof(rowGroupOrdinal));
        var rowGroup = _metadata.RowGroup(rowGroupOrdinal);
        ValidateOrdinal(columnOrdinal, rowGroup.ColumnCount, nameof(columnOrdinal));
        return BloomFilterReader.Open(this, _metadata.ColumnChunk(rowGroupOrdinal, columnOrdinal));
    }

    internal ParquetPageCursor OpenPages(int rowGroupOrdinal, int columnOrdinal,
        PageMetadataHandle pageMetadata, ParquetPagePruner pruner)
    {
        ValidateGeneration(_generation);
        ValidateOrdinal(rowGroupOrdinal, _metadata.RowGroupCount, nameof(rowGroupOrdinal));
        ArgumentNullException.ThrowIfNull(pruner);
        return new ParquetPageCursor(this, _generation, rowGroupOrdinal, columnOrdinal, pageMetadata, pruner);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _generation++;
        ReturnMetadataBuffers();
        DisposeOwnedStream();
        _ownedSource = null;
        _source = null;
    }

    void DisposeOwnedStream()
    {
        var stream = _ownedStream;
        _ownedStream = null;
        stream?.Dispose();
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
            _metadata.FooterBuffer = Rent(footerLength);
            _metadata.FooterByteCount = checked((int)footerLength);
            var footerBytes = _metadata.FooterBuffer.Span[.._metadata.FooterByteCount];
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

    internal bool VerifyPageCrc
        => _options.VerifyPageCrc;

    internal IParquetReadSource Source
    {
        get
        {
            ThrowIfDisposed();
            return _source ?? throw new InvalidOperationException("The reader has not been reset with a source.");
        }
    }

    internal bool TryBorrowSource(ulong offset, int length, out ReadOnlyMemory<byte> bytes)
    {
        ThrowIfDisposed();
        if (_source is MemoryReadSource source)
            return source.TryBorrow(offset, length, out bytes);

        bytes = default;
        return false;
    }

    ParquetBuffer Rent(uint count)
        => _options.BufferPool.Rent(count);

    void ReturnMetadataBuffers()
        => _metadata.ReturnBuffers();

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
