namespace Plank2;

public interface IParquetLog
{
    void PageListCapacityGrew(int previousCapacity, int newCapacity);
}
