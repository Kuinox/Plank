namespace Plank;

public interface IParquetLog
{
    void RowGroupMetadataCapacityGrown(int previousCapacity, int newCapacity, int? expectedRowGroupCount);

    void FooterBufferCapacityGrown(int previousCapacity, int newCapacity, int requiredCapacity);

    void StreamWriteObserved(int byteCount, long writeDurationTicks, long writeGapTicks);

    void ColumnWriteMetricsObserved(string columnName, int rowCount, int valueCount, int bytesWritten, long encodeTicks, long compressTicks, long waitForWriteTicks, long writeTicks);
}
