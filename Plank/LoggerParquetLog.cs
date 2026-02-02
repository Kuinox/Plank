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

    public void RowGroupMetadataCapacityGrownWithoutEstimate(int previousCapacity, int newCapacity)
        => _logger.LogDebug(
            "Row group metadata capacity grew from {PreviousCapacity} to {NewCapacity} because no row group count was specified.",
            previousCapacity,
            newCapacity);

    public void RowGroupMetadataCapacityGrownBeyondEstimate(int previousCapacity, int newCapacity, int expectedRowGroupCount)
        => _logger.LogDebug(
            "Row group metadata capacity grew from {PreviousCapacity} to {NewCapacity} because the expected row group count {ExpectedRowGroupCount} was underspecified.",
            previousCapacity,
            newCapacity,
            expectedRowGroupCount);
}
