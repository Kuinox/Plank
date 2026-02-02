namespace Plank;

public interface IParquetLog
{
    void RowGroupMetadataCapacityGrown(int previousCapacity, int newCapacity, int? expectedRowGroupCount);
}
