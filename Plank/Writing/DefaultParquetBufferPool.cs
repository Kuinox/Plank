using System.Buffers;

namespace Plank.Writing;

public sealed class DefaultParquetBufferPool : IParquetBufferPool
{
    public static readonly DefaultParquetBufferPool Shared = new();

    DefaultParquetBufferPool()
    {
    }

    public byte[] Rent(int minimumLength)
        => ArrayPool<byte>.Shared.Rent(minimumLength);

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
