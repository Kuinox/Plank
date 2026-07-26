namespace Plank;

public interface IParquetBufferPool
{
    ParquetBuffer Rent(uint minimumByteLength);
}
