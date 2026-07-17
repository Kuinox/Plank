using System.Runtime.CompilerServices;

namespace Plank.Writing;

public static class ParquetBufferPoolExtensions
{
    public static ParquetBuffer Rent<T>(this IParquetBufferPool bufferPool, uint minimumLength)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(bufferPool);
        return bufferPool.Rent(checked(minimumLength * (uint)Unsafe.SizeOf<T>()));
    }
}
