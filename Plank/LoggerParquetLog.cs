using Microsoft.Extensions.Logging;

namespace Plank;

public sealed class LoggerParquetLog : IParquetLog
{
    static readonly Action<ILogger, int, int, int, Exception?> RowGroupMetadataExpected =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Debug,
            new EventId(1, nameof(RowGroupMetadataCapacityGrown)),
            "Row group metadata capacity grew from {PreviousCapacity} to {NewCapacity} because the expected row group count {ExpectedRowGroupCount} was underspecified.");

    static readonly Action<ILogger, int, int, Exception?> RowGroupMetadataUnspecified =
        LoggerMessage.Define<int, int>(
            LogLevel.Debug,
            new EventId(2, nameof(RowGroupMetadataCapacityGrown)),
            "Row group metadata capacity grew from {PreviousCapacity} to {NewCapacity} because no row group count was specified.");

    static readonly Action<ILogger, int, int, int, Exception?> FooterBufferGrown =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Warning,
            new EventId(3, nameof(FooterBufferCapacityGrown)),
            "Footer buffer capacity grew from {PreviousCapacity} to {NewCapacity} because {RequiredCapacity} bytes were required.");
    static readonly Action<ILogger, int, long, long, Exception?> StreamWriteObservedEvent =
        LoggerMessage.Define<int, long, long>(
            LogLevel.Trace,
            new EventId(4, nameof(StreamWriteObserved)),
            "Stream write observed: {ByteCount} bytes, duration ticks {WriteDurationTicks}, gap ticks {WriteGapTicks}.");

    readonly ILogger _logger;

    public LoggerParquetLog(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void RowGroupMetadataCapacityGrown(int previousCapacity, int newCapacity, int? expectedRowGroupCount)
    {
        if (expectedRowGroupCount.HasValue)
            RowGroupMetadataExpected(_logger, previousCapacity, newCapacity, expectedRowGroupCount.Value, null);
        else
            RowGroupMetadataUnspecified(_logger, previousCapacity, newCapacity, null);
    }

    public void FooterBufferCapacityGrown(int previousCapacity, int newCapacity, int requiredCapacity)
        => FooterBufferGrown(_logger, previousCapacity, newCapacity, requiredCapacity, null);

    public void StreamWriteObserved(int byteCount, long writeDurationTicks, long writeGapTicks)
        => StreamWriteObservedEvent(_logger, byteCount, writeDurationTicks, writeGapTicks, null);

    public void ColumnWriteMetricsObserved(string columnName, int rowCount, int valueCount, int bytesWritten, long encodeTicks, long compressTicks, long waitForWriteTicks, long writeTicks)
        => _logger.LogTrace(
            new EventId(5, nameof(ColumnWriteMetricsObserved)),
            "Column metrics: {ColumnName}, rows {RowCount}, values {ValueCount}, bytes {BytesWritten}, encode {EncodeTicks}, compress {CompressTicks}, wait {WaitForWriteTicks}, write {WriteTicks}.",
            columnName,
            rowCount,
            valueCount,
            bytesWritten,
            encodeTicks,
            compressTicks,
            waitForWriteTicks,
            writeTicks);

    public void StringEncodingMetricsObserved(string columnName, int rowCount, int nonNullCount, long sizePassTicks, long definitionLevelsTicks, long byteCountPassTicks, long utf8WritePassTicks)
        => _logger.LogTrace(
            new EventId(6, nameof(StringEncodingMetricsObserved)),
            "String encoding metrics: {ColumnName}, rows {RowCount}, nonNull {NonNullCount}, size {SizePassTicks}, def {DefinitionLevelsTicks}, byteCount {ByteCountPassTicks}, utf8 {Utf8WritePassTicks}.",
            columnName,
            rowCount,
            nonNullCount,
            sizePassTicks,
            definitionLevelsTicks,
            byteCountPassTicks,
            utf8WritePassTicks);
}
