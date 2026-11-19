using System.Runtime.CompilerServices;

namespace Plank.Writing;

internal readonly struct BufferWriterFactory
{
    internal readonly IParquetBufferPool BufferPool;
    readonly uint _bufferChunkSizeBytes;
    readonly uint _initialPageBufferBytes;
    readonly uint _initialColumnBufferBytes;
    readonly uint _initialMetadataBufferBytes;
    readonly uint _pageChunkSizeBytes;
    readonly uint _initialPageContentBytes;

    // Smallest content chunk used for small target pages.
    const uint MinimumPageChunkBytes = 4096;

    internal BufferWriterFactory(IParquetBufferPool bufferPool, uint bufferChunkSizeBytes, uint initialPageBufferBytes,
        uint initialColumnBufferBytes, uint initialMetadataBufferBytes, uint targetDataPageSizeBytes = uint.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(bufferPool);
        if (bufferChunkSizeBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(bufferChunkSizeBytes), bufferChunkSizeBytes,
                "Buffer chunk size must be greater than zero.");
        if (initialPageBufferBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(initialPageBufferBytes), initialPageBufferBytes,
                "Initial page buffer size must be greater than zero.");
        if (initialColumnBufferBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(initialColumnBufferBytes), initialColumnBufferBytes,
                "Initial column buffer size must be greater than zero.");
        if (initialMetadataBufferBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(initialMetadataBufferBytes), initialMetadataBufferBytes,
                "Initial metadata buffer size must be greater than zero.");

        BufferPool = bufferPool;
        _bufferChunkSizeBytes = bufferChunkSizeBytes;
        _initialPageBufferBytes = initialPageBufferBytes;
        _initialColumnBufferBytes = initialColumnBufferBytes;
        _initialMetadataBufferBytes = initialMetadataBufferBytes;
        // Every page keeps its content writer until the row group is written. With small target pages a full chunk
        // per page dwarfed the data, so their chunks are sized to twice the target (pages stop just past it).
        // Targets of at least half a chunk keep the configured chunk, so the default layout is unchanged.
        if (targetDataPageSizeBytes < bufferChunkSizeBytes / 2)
        {
            _pageChunkSizeBytes = Math.Min(bufferChunkSizeBytes,
                Math.Max(MinimumPageChunkBytes, targetDataPageSizeBytes * 2));
            _initialPageContentBytes = _pageChunkSizeBytes;
        }
        else
        {
            _pageChunkSizeBytes = bufferChunkSizeBytes;
            _initialPageContentBytes = initialPageBufferBytes;
        }
    }

    internal BufferWriter CreatePageBufferWriter()
        => new(BufferPool, _pageChunkSizeBytes, _initialPageContentBytes);

    // A page header is a few dozen bytes, plus its statistics; rarely more than this. Each page owns its header
    // writer until the row group is written, so a full chunk per header multiplied memory for many small pages.
    const uint PageHeaderChunkBytes = 1024;

    internal BufferWriter CreatePageHeaderBufferWriter()
    {
        var chunk = Math.Min(_bufferChunkSizeBytes, PageHeaderChunkBytes);
        return new(BufferPool, chunk, chunk);
    }

    internal BufferWriter CreateColumnBufferWriter()
        => new(BufferPool, _bufferChunkSizeBytes, _initialColumnBufferBytes);

    internal BufferWriter CreateMetadataBufferWriter()
        => new(BufferPool, _bufferChunkSizeBytes, _initialMetadataBufferBytes);

    internal ParquetBuffer RentScratch<T>(uint minimumLength)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new NotSupportedException($"{typeof(T)} cannot be stored in a ParquetBuffer.");
        return BufferPool.Rent(checked(minimumLength * (uint)Unsafe.SizeOf<T>()));
    }

    internal ParquetBuffer RentScratch(uint minimumByteLength)
        => BufferPool.Rent(minimumByteLength);

    internal void ReturnScratch(ParquetBuffer buffer)
        => buffer.Dispose();
}
