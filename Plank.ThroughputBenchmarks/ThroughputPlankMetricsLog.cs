using Plank.Schema;
using Plank.Writing;
using System.Diagnostics;

namespace Plank.Benchmarks;

public sealed class ThroughputPlankMetricsLog : IParquetLog
{
    static readonly ColumnOptions RequiredPlain = new(ParquetRepetition.Required, [EncodingKind.Plain]);
    static readonly ColumnOptions OptionalPlain = new(ParquetRepetition.Optional, [EncodingKind.Plain]);
    static readonly ParquetSchema MetricsSchema = new([
        new Column("event_index", ParquetPhysicalType.Int32, RequiredPlain),
        new Column("event_type", ParquetPhysicalType.ByteArray, RequiredPlain),
        new Column("bytes", ParquetPhysicalType.Int64, OptionalPlain),
        new Column("gap_ticks", ParquetPhysicalType.Int64, RequiredPlain),
        new Column("write_ticks", ParquetPhysicalType.Int64, OptionalPlain),
        new Column("flush_ticks", ParquetPhysicalType.Int64, OptionalPlain),
        new Column("time_ticks", ParquetPhysicalType.Int64, RequiredPlain),
        new Column("time_ms", ParquetPhysicalType.Double, RequiredPlain),
        new Column("cumulative_bytes", ParquetPhysicalType.Int64, RequiredPlain),
        new Column("write_mib_per_s", ParquetPhysicalType.Double, OptionalPlain),
        new Column("column_name", ParquetPhysicalType.ByteArray, OptionalPlain),
        new Column("column_row_count", ParquetPhysicalType.Int32, OptionalPlain),
        new Column("column_value_count", ParquetPhysicalType.Int32, OptionalPlain),
        new Column("column_encode_ticks", ParquetPhysicalType.Int64, OptionalPlain),
        new Column("column_compress_ticks", ParquetPhysicalType.Int64, OptionalPlain),
        new Column("column_wait_for_write_ticks", ParquetPhysicalType.Int64, OptionalPlain),
        new Column("column_write_ticks", ParquetPhysicalType.Int64, OptionalPlain),
        new Column("column_start_ms", ParquetPhysicalType.Double, OptionalPlain),
        new Column("column_end_ms", ParquetPhysicalType.Double, OptionalPlain)
    ]);

    readonly List<StreamWriteMetricSample> _writeSamples = [];
    readonly List<FlushMetricSample> _flushSamples = [];
    readonly List<ColumnWriteMetricSample> _columnSamples = [];
    readonly List<StringEncodingMetricSample> _stringEncodingSamples = [];
    long _cumulativeTicks;
    long _originTimestamp;
    bool _hasOriginTimestamp;

    public IReadOnlyList<StreamWriteMetricSample> WriteSamples => _writeSamples;

    public IReadOnlyList<FlushMetricSample> FlushSamples => _flushSamples;

    public IReadOnlyList<ColumnWriteMetricSample> ColumnSamples => _columnSamples;

    public IReadOnlyList<StringEncodingMetricSample> StringEncodingSamples => _stringEncodingSamples;

    public void RowGroupMetadataCapacityGrown(int previousCapacity, int newCapacity, int? expectedRowGroupCount)
    {
    }

    public void FooterBufferCapacityGrown(int previousCapacity, int newCapacity, int requiredCapacity)
    {
    }

    public void StreamWriteObserved(int byteCount, long writeDurationTicks, long writeGapTicks)
    {
        EnsureOrigin();
        _cumulativeTicks = checked(_cumulativeTicks + writeGapTicks + writeDurationTicks);
        _writeSamples.Add(new StreamWriteMetricSample(
            Index: _writeSamples.Count,
            ByteCount: byteCount,
            WriteGapTicks: writeGapTicks,
            WriteDurationTicks: writeDurationTicks,
            CumulativeTicksAfterWrite: _cumulativeTicks));
    }

    public void ColumnWriteMetricsObserved(string columnName, int rowCount, int valueCount, int bytesWritten, long encodeTicks, long compressTicks, long waitForWriteTicks, long writeTicks)
    {
        var now = Stopwatch.GetTimestamp();
        if (!_hasOriginTimestamp)
        {
            _originTimestamp = now;
            _hasOriginTimestamp = true;
        }
        var totalTicks = checked(encodeTicks + compressTicks + waitForWriteTicks + writeTicks);
        var endMs = ToRelativeMilliseconds(now);
        var startMs = endMs - (totalTicks * 1_000d / Stopwatch.Frequency);
        _columnSamples.Add(new ColumnWriteMetricSample(
            Index: _columnSamples.Count,
            ColumnName: columnName,
            RowCount: rowCount,
            ValueCount: valueCount,
            BytesWritten: bytesWritten,
            EncodeTicks: encodeTicks,
            CompressTicks: compressTicks,
            WaitForWriteTicks: waitForWriteTicks,
            WriteTicks: writeTicks,
            StartMs: startMs,
            EndMs: endMs));
    }

    public void StringEncodingMetricsObserved(string columnName, int rowCount, int nonNullCount, long sizePassTicks, long definitionLevelsTicks, long byteCountPassTicks, long utf8WritePassTicks)
    {
        _stringEncodingSamples.Add(new StringEncodingMetricSample(
            Index: _stringEncodingSamples.Count,
            ColumnName: columnName,
            RowCount: rowCount,
            NonNullCount: nonNullCount,
            SizePassTicks: sizePassTicks,
            DefinitionLevelsTicks: definitionLevelsTicks,
            ByteCountPassTicks: byteCountPassTicks,
            Utf8WritePassTicks: utf8WritePassTicks));
    }

    public void FlushObserved(long flushDurationTicks, long flushGapTicks = 0)
    {
        EnsureOrigin();
        _cumulativeTicks = checked(_cumulativeTicks + flushGapTicks + flushDurationTicks);
        _flushSamples.Add(new FlushMetricSample(
            Index: _flushSamples.Count,
            FlushDurationTicks: flushDurationTicks,
            FlushGapTicks: flushGapTicks,
            CumulativeTicksAfterFlush: _cumulativeTicks));
    }

    public async Task WriteParquetAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var rows = BuildRows();
        if (rows.Count == 0)
            return;

        var eventIndex = new int[rows.Count];
        var eventType = new string[rows.Count];
        var bytes = new long?[rows.Count];
        var gapTicks = new long[rows.Count];
        var writeTicks = new long?[rows.Count];
        var flushTicks = new long?[rows.Count];
        var timeTicks = new long[rows.Count];
        var timeMs = new double[rows.Count];
        var cumulativeBytes = new long[rows.Count];
        var writeMiBPerSecond = new double?[rows.Count];
        var columnName = new string?[rows.Count];
        var columnRowCount = new int?[rows.Count];
        var columnValueCount = new int?[rows.Count];
        var columnEncodeTicks = new long?[rows.Count];
        var columnCompressTicks = new long?[rows.Count];
        var columnWaitForWriteTicks = new long?[rows.Count];
        var columnWriteTicks = new long?[rows.Count];
        var columnStartMs = new double?[rows.Count];
        var columnEndMs = new double?[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            eventIndex[i] = row.EventIndex;
            eventType[i] = row.EventType;
            bytes[i] = row.Bytes;
            gapTicks[i] = row.GapTicks;
            writeTicks[i] = row.WriteTicks;
            flushTicks[i] = row.FlushTicks;
            timeTicks[i] = row.TimeTicks;
            timeMs[i] = row.TimeMs;
            cumulativeBytes[i] = row.CumulativeBytes;
            writeMiBPerSecond[i] = row.WriteMiBPerSecond;
            columnName[i] = row.ColumnName;
            columnRowCount[i] = row.ColumnRowCount;
            columnValueCount[i] = row.ColumnValueCount;
            columnEncodeTicks[i] = row.ColumnEncodeTicks;
            columnCompressTicks[i] = row.ColumnCompressTicks;
            columnWaitForWriteTicks[i] = row.ColumnWaitForWriteTicks;
            columnWriteTicks[i] = row.ColumnWriteTicks;
            columnStartMs[i] = row.ColumnStartMs;
            columnEndMs[i] = row.ColumnEndMs;
        }

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 256 * 1024,
            Options = FileOptions.Asynchronous
        });
        using var writer = ParquetWriter.Create(stream, MetricsSchema, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime,
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = checked((uint)rows.Count),
            Log = ParquetLog.None
        });
        var rowGroup = writer.StartRowGroup();
        await rowGroup.WriteAsync(MetricsSchema.Columns[0], eventIndex).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[1], eventType).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[2], bytes).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[3], gapTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[4], writeTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[5], flushTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[6], timeTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[7], timeMs).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[8], cumulativeBytes).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[9], writeMiBPerSecond).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[10], columnName).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[11], columnRowCount).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[12], columnValueCount).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[13], columnEncodeTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[14], columnCompressTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[15], columnWaitForWriteTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[16], columnWriteTicks).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[17], columnStartMs).ConfigureAwait(false);
        await rowGroup.WriteAsync(MetricsSchema.Columns[18], columnEndMs).ConfigureAwait(false);
        writer.CloseFile(cancellationToken);
    }

    List<MetricRow> BuildRows()
    {
        var rows = new List<MetricRow>(_writeSamples.Count + _flushSamples.Count);
        var frequency = Stopwatch.Frequency;
        var cumulativeBytes = 0L;
        var writeIndex = 0;
        var flushIndex = 0;
        while (writeIndex < _writeSamples.Count || flushIndex < _flushSamples.Count)
        {
            var takeWrite = flushIndex >= _flushSamples.Count
                || (writeIndex < _writeSamples.Count && _writeSamples[writeIndex].CumulativeTicksAfterWrite <= _flushSamples[flushIndex].CumulativeTicksAfterFlush);
            if (takeWrite)
            {
                var write = _writeSamples[writeIndex];
                cumulativeBytes = checked(cumulativeBytes + write.ByteCount);
                var writeSeconds = write.WriteDurationTicks / (double)frequency;
                var throughput = writeSeconds > 0d ? (write.ByteCount / (1024d * 1024d)) / writeSeconds : 0d;
                rows.Add(new MetricRow(
                    rows.Count,
                    "write",
                    write.ByteCount,
                    write.WriteGapTicks,
                    write.WriteDurationTicks,
                    null,
                    write.CumulativeTicksAfterWrite,
                    write.CumulativeTicksAfterWrite * 1_000d / frequency,
                    cumulativeBytes,
                    throughput,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
                writeIndex++;
                continue;
            }

            var flush = _flushSamples[flushIndex];
            rows.Add(new MetricRow(
                rows.Count,
                "flush",
                null,
                flush.FlushGapTicks,
                null,
                flush.FlushDurationTicks,
                flush.CumulativeTicksAfterFlush,
                flush.CumulativeTicksAfterFlush * 1_000d / frequency,
                cumulativeBytes,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
            flushIndex++;
        }

        foreach (var column in _columnSamples)
            rows.Add(new MetricRow(
                rows.Count,
                "column",
                column.BytesWritten,
                column.WaitForWriteTicks,
                column.WriteTicks,
                null,
                TimeTicks: 0,
                TimeMs: 0,
                CumulativeBytes: 0,
                WriteMiBPerSecond: null,
                ColumnName: column.ColumnName,
                ColumnRowCount: column.RowCount,
                ColumnValueCount: column.ValueCount,
                ColumnEncodeTicks: column.EncodeTicks,
                ColumnCompressTicks: column.CompressTicks,
                ColumnWaitForWriteTicks: column.WaitForWriteTicks,
                ColumnWriteTicks: column.WriteTicks,
                ColumnStartMs: column.StartMs,
                ColumnEndMs: column.EndMs));

        foreach (var stringEncoding in _stringEncodingSamples)
            rows.Add(new MetricRow(
                rows.Count,
                "string_encoding",
                null,
                0,
                null,
                null,
                TimeTicks: 0,
                TimeMs: 0,
                CumulativeBytes: 0,
                WriteMiBPerSecond: null,
                ColumnName: stringEncoding.ColumnName,
                ColumnRowCount: stringEncoding.RowCount,
                ColumnValueCount: stringEncoding.NonNullCount,
                ColumnEncodeTicks: stringEncoding.SizePassTicks,
                ColumnCompressTicks: stringEncoding.DefinitionLevelsTicks,
                ColumnWaitForWriteTicks: stringEncoding.ByteCountPassTicks,
                ColumnWriteTicks: stringEncoding.Utf8WritePassTicks,
                ColumnStartMs: null,
                ColumnEndMs: null));

        return rows;
    }

    readonly record struct MetricRow(
        int EventIndex,
        string EventType,
        long? Bytes,
        long GapTicks,
        long? WriteTicks,
        long? FlushTicks,
        long TimeTicks,
        double TimeMs,
        long CumulativeBytes,
        double? WriteMiBPerSecond,
        string? ColumnName,
        int? ColumnRowCount,
        int? ColumnValueCount,
        long? ColumnEncodeTicks,
        long? ColumnCompressTicks,
        long? ColumnWaitForWriteTicks,
        long? ColumnWriteTicks,
        double? ColumnStartMs,
        double? ColumnEndMs);

    void EnsureOrigin()
    {
        if (_hasOriginTimestamp)
            return;
        _originTimestamp = Stopwatch.GetTimestamp();
        _hasOriginTimestamp = true;
    }

    double ToRelativeMilliseconds(long timestamp)
        => _hasOriginTimestamp ? (timestamp - _originTimestamp) * 1_000d / Stopwatch.Frequency : 0d;
}
