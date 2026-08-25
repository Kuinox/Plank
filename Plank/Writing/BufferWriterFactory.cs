using System.Runtime.CompilerServices;

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
