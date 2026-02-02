namespace Plank;

public sealed class ParquetLog : IParquetLog
{
    public static readonly ParquetLog None = new();

    ParquetLog()
    {
    }

    public void RowGroupMetadataCapacityGrownWithoutEstimate(int previousCapacity, int newCapacity)
    {
    }

    public void RowGroupMetadataCapacityGrownBeyondEstimate(int previousCapacity, int newCapacity, int expectedRowGroupCount)
    {
    }
}
