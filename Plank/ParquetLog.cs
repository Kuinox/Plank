namespace Plank;

public sealed class ParquetLog : IParquetLog
{
    public static readonly ParquetLog None = new();

    ParquetLog()
    {
    }

    public void RowGroupMetadataCapacityGrown(int previousCapacity, int newCapacity, int? expectedRowGroupCount)
    {
    }
}
