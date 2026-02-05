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
}
