using Microsoft.Extensions.Logging;

namespace Plank;

public sealed class LoggerParquetLog : IParquetLog
{
    readonly ILogger _logger;

    public LoggerParquetLog(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void RowGroupMetadataCapacityGrown(int previousCapacity, int newCapacity, int? expectedRowGroupCount)
    {
        if (expectedRowGroupCount.HasValue)
            _logger.LogDebug(
                "Row group metadata capacity grew from {PreviousCapacity} to {NewCapacity} because the expected row group count {ExpectedRowGroupCount} was underspecified.",
                previousCapacity,
                newCapacity,
                expectedRowGroupCount.Value);
        else
            _logger.LogDebug(
                "Row group metadata capacity grew from {PreviousCapacity} to {NewCapacity} because no row group count was specified.",
                previousCapacity,
                newCapacity);
    }
}
