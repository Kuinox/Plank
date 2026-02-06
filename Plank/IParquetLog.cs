namespace Plank;

public interface IParquetLog
{
    void RowGroupMetadataCapacityGrown(int previousCapacity, int newCapacity, int? expectedRowGroupCount);

    void FooterBufferCapacityGrown(int previousCapacity, int newCapacity, int requiredCapacity);
}
