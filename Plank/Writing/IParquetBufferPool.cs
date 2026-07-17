namespace Plank.Writing;

public interface IParquetBufferPool
{
    ParquetBuffer Rent(uint minimumByteLength);
}
