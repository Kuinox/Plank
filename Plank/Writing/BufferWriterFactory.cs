namespace Plank.Writing;

internal readonly struct BufferWriterFactory
{
    internal readonly IParquetBufferPool BufferPool;
    readonly uint _bufferChunkSizeBytes;
    readonly uint _initialPageBufferBytes;
    readonly uint _initialColumnBufferBytes;
    readonly uint _initialMetadataBufferBytes;

    internal BufferWriterFactory(IParquetBufferPool bufferPool, uint bufferChunkSizeBytes, uint initialPageBufferBytes,
        uint initialColumnBufferBytes, uint initialMetadataBufferBytes)
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
    }

    internal BufferWriter CreatePageBufferWriter()
        => new(BufferPool, _bufferChunkSizeBytes, _initialPageBufferBytes);

    internal BufferWriter CreateColumnBufferWriter()
        => new(BufferPool, _bufferChunkSizeBytes, _initialColumnBufferBytes);

    internal BufferWriter CreateMetadataBufferWriter()
        => new(BufferPool, _bufferChunkSizeBytes, _initialMetadataBufferBytes);

    internal T[] RentScratch<T>(uint minimumLength)
        => ArrayRenter<T>.Shared.Rent(minimumLength);

    internal byte[] RentScratch(uint minimumLength)
        => RentScratch<byte>(minimumLength);

    internal void ReturnScratch<T>(T[] buffer, bool clearArray = false)
        => ArrayRenter<T>.Shared.Return(buffer, clearArray);
}
