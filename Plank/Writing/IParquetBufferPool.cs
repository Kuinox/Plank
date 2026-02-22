namespace Plank.Writing;

public interface IParquetBufferPool
{
    byte[] Rent(uint minimumLength);

    void Return(byte[] buffer);
}
