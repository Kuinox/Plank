namespace Plank.Writing;

public sealed class DefaultParquetBufferPool : IParquetBufferPool
{
    public static readonly DefaultParquetBufferPool Shared = new();

    DefaultParquetBufferPool()
    {
    }

    public byte[] Rent(uint minimumLength)
        => ArrayRenter<byte>.Shared.Rent(checked((int)minimumLength));

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArrayRenter<byte>.Shared.Return(buffer);
    }
}
