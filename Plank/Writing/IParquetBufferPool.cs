namespace Plank.Writing;

public interface IParquetBufferPool
{
    T[] Rent<T>(uint minimumLength);

    void Return<T>(T[] buffer, bool clearArray = false);
}
