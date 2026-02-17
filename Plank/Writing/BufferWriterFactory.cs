namespace Plank.Writing;

internal readonly struct BufferWriterFactory
{
    readonly IParquetBufferPool _bufferPool;
    readonly int _bufferChunkSizeBytes;
    readonly int _initialPageBufferBytes;
    readonly int _initialColumnBufferBytes;
    readonly int _initialMetadataBufferBytes;

    internal BufferWriterFactory(IParquetBufferPool bufferPool, int bufferChunkSizeBytes, int initialPageBufferBytes,
        int initialColumnBufferBytes, int initialMetadataBufferBytes)
    {
        ArgumentNullException.ThrowIfNull(bufferPool);
        if (bufferChunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferChunkSizeBytes), bufferChunkSizeBytes,
                "Buffer chunk size must be greater than zero.");
        if (initialPageBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialPageBufferBytes), initialPageBufferBytes,
                "Initial page buffer size must be greater than zero.");
        if (initialColumnBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialColumnBufferBytes), initialColumnBufferBytes,
                "Initial column buffer size must be greater than zero.");
        if (initialMetadataBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialMetadataBufferBytes), initialMetadataBufferBytes,
                "Initial metadata buffer size must be greater than zero.");

        _bufferPool = bufferPool;
        _bufferChunkSizeBytes = bufferChunkSizeBytes;
        _initialPageBufferBytes = initialPageBufferBytes;
        _initialColumnBufferBytes = initialColumnBufferBytes;
        _initialMetadataBufferBytes = initialMetadataBufferBytes;
    }

    internal BufferWriter CreatePageBufferWriter()
        => new(_bufferPool, _bufferChunkSizeBytes, _initialPageBufferBytes);

    internal BufferWriter CreateColumnBufferWriter()
        => new(_bufferPool, _bufferChunkSizeBytes, _initialColumnBufferBytes);

    internal BufferWriter CreateMetadataBufferWriter()
        => new(_bufferPool, _bufferChunkSizeBytes, _initialMetadataBufferBytes);

    internal byte[] RentScratch(int minimumLength)
        => _bufferPool.Rent(minimumLength);

    internal void ReturnScratch(byte[] buffer)
        => _bufferPool.Return(buffer);
}
