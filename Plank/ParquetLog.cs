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

    public void FooterBufferCapacityGrown(int previousCapacity, int newCapacity, int requiredCapacity)
    {
    }

    public void StreamWriteObserved(int byteCount, long writeDurationTicks, long writeGapTicks)
    {
    }

    public void ColumnWriteMetricsObserved(string columnName, int rowCount, int valueCount, int bytesWritten, long encodeTicks, long compressTicks, long waitForWriteTicks, long writeTicks)
    {
    }
}
