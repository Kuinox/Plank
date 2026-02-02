namespace Plank;

public interface IParquetLog
{
    void RowGroupMetadataCapacityGrownWithoutEstimate(int previousCapacity, int newCapacity);

    void RowGroupMetadataCapacityGrownBeyondEstimate(int previousCapacity, int newCapacity, int expectedRowGroupCount);
}
