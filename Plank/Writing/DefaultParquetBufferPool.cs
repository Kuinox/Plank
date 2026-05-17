namespace Plank.Writing;

public sealed class DefaultParquetBufferPool : IParquetBufferPool
{
    public static readonly DefaultParquetBufferPool Shared = new();

    DefaultParquetBufferPool()
    {
    }

    public T[] Rent<T>(uint minimumLength)
        => ArrayRenter<T>.Shared.Rent(minimumLength);

    public void Return<T>(T[] buffer, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArrayRenter<T>.Shared.Return(buffer, clearArray);
    }
}
