namespace Plank2;

public sealed class ParquetLog : IParquetLog
{
    public static readonly ParquetLog None = new();

    public void PageListCapacityGrew(int previousCapacity, int newCapacity)
    {
    }
}
