using System.Diagnostics;

namespace Plank.ThroughputBenchmarks;

public sealed class ThroughputPlankMetricsLog
{
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
        // TODO: restore Parquet metrics output once nullable encoding and logging hooks are reintroduced.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var rows = BuildRows();
        if (rows.Count == 0)
            return;

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 256 * 1024,
            Options = FileOptions.Asynchronous
        });
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("event_index\tevent_type\tbytes\tgap_ticks\twrite_ticks\tflush_ticks\ttime_ticks\ttime_ms\tcumulative_bytes\twrite_mib_per_s\tcolumn_name\tcolumn_row_count\tcolumn_value_count\tcolumn_encode_ticks\tcolumn_compress_ticks\tcolumn_wait_for_write_ticks\tcolumn_write_ticks\tcolumn_start_ms\tcolumn_end_ms").ConfigureAwait(false);
        for (var i = 0; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            await writer.WriteLineAsync(
                $"{row.EventIndex}\t{Escape(row.EventType)}\t{Format(row.Bytes)}\t{row.GapTicks}\t{Format(row.WriteTicks)}\t{Format(row.FlushTicks)}\t{row.TimeTicks}\t{row.TimeMs}\t{row.CumulativeBytes}\t{Format(row.WriteMiBPerSecond)}\t{Escape(row.ColumnName)}\t{Format(row.ColumnRowCount)}\t{Format(row.ColumnValueCount)}\t{Format(row.ColumnEncodeTicks)}\t{Format(row.ColumnCompressTicks)}\t{Format(row.ColumnWaitForWriteTicks)}\t{Format(row.ColumnWriteTicks)}\t{Format(row.ColumnStartMs)}\t{Format(row.ColumnEndMs)}").ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    static string Escape(string? value)
        => value is null ? string.Empty : value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    static string Format<T>(T? value)
        where T : struct
        => value.HasValue ? value.Value.ToString()! : string.Empty;

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
