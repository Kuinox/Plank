namespace Plank2.Writing;

public interface IParquetBufferPool
{
    byte[] Rent(int minimumLength);

    void Return(byte[] buffer);
}
