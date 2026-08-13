using System.Runtime.InteropServices;
using Apache.Arrow;
using ParquetSharp.Arrow;
using ParquetSharp.IO;
using ArrowFileReader = ParquetSharp.Arrow.FileReader;
using NativeBuffer = ParquetSharp.IO.Buffer;

namespace Plank.Benchmarks.Published;

sealed class ParquetSharpPublishedBenchmarkReader : IPublishedBenchmarkReader
{
    readonly PublishedBenchmarkDataSet _dataSet;
    readonly bool _useThreads;
    readonly int _workerCount;
    readonly GCHandle _pinnedBytes;
    readonly NativeBuffer _buffer;
    readonly BufferReader _bufferReader;

    public ParquetSharpPublishedBenchmarkReader(byte[] fileBytes, PublishedBenchmarkDataSet dataSet,
        bool useThreads, int workerCount)
    {
        _dataSet = dataSet;
        _useThreads = useThreads;
        _workerCount = workerCount;
        _pinnedBytes = GCHandle.Alloc(fileBytes, GCHandleType.Pinned);
        _buffer = new NativeBuffer(_pinnedBytes.AddrOfPinnedObject(), fileBytes.LongLength);
        _bufferReader = new BufferReader(_buffer);
    }

    public string ImplementationId
        => _useThreads ? "parquetsharp-multi" : "parquetsharp-single";

    public string Label
        => _useThreads ? $"ParquetSharp ({_workerCount} threads)" : "ParquetSharp (1 thread)";

    public int Threads
        => _useThreads ? _workerCount : 1;

    public bool IsSupported
        => true;

    public string? UnavailableReason
        => null;

    public async ValueTask<PublishedReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        using var readerProperties = ParquetSharp.ReaderProperties.GetDefaultReaderProperties();
        using var arrowProperties = ArrowReaderProperties.GetDefault();
        arrowProperties.UseThreads = _useThreads;
        arrowProperties.PreBuffer = false;
        arrowProperties.BatchSize = int.MaxValue;
        using var reader = new ArrowFileReader(_bufferReader, readerProperties, arrowProperties);
        if (reader.NumRowGroups != _dataSet.RowGroupCount)
            throw new InvalidDataException(
                $"ParquetSharp found {reader.NumRowGroups} row groups instead of {_dataSet.RowGroupCount}.");

        var aggregate = PublishedReadFingerprint.Start();
        long valueCount = 0;
        for (var rowGroupIndex = 0; rowGroupIndex < reader.NumRowGroups; rowGroupIndex++)
        {
            using var batches = reader.GetRecordBatchReader([rowGroupIndex]);
            using var batch = await batches.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"ParquetSharp returned no batch for row group {rowGroupIndex}.");
            if (batch.ColumnCount != _dataSet.Columns.Count)
                throw new InvalidDataException(
                    $"ParquetSharp decoded {batch.ColumnCount} columns instead of {_dataSet.Columns.Count}.");
            for (var columnIndex = 0; columnIndex < batch.ColumnCount; columnIndex++)
            {
                var piece = ReadColumn(batch.Column(columnIndex), _dataSet.Columns[columnIndex].Kind,
                    columnIndex, rowGroupIndex, batch.Length);
                aggregate = PublishedReadFingerprint.Combine(aggregate, piece);
                valueCount = checked(valueCount + piece.ValueCount);
            }
            using var extraBatch = await batches.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (extraBatch is not null)
                throw new InvalidDataException(
                    $"ParquetSharp split row group {rowGroupIndex} into more than one batch.");
        }
        return new PublishedReadResult(valueCount, aggregate);
    }

    public void Dispose()
    {
        _bufferReader.Dispose();
        _buffer.Dispose();
        if (_pinnedBytes.IsAllocated)
            _pinnedBytes.Free();
    }

    static PublishedReadResult ReadColumn(IArrowArray array, BenchmarkColumnKind kind,
        int columnIndex, int rowGroupIndex, int rowCount)
    {
        if (array.Length != rowCount)
            throw new InvalidDataException(
                $"ParquetSharp decoded {array.Length} values instead of {rowCount} for column {columnIndex}.");
        var fingerprint = PublishedReadFingerprint.StartPiece(columnIndex, rowGroupIndex, rowCount);
        for (var sampleIndex = 0; sampleIndex < 3 && rowCount != 0; sampleIndex++)
        {
            var position = PublishedReadFingerprint.SamplePosition(sampleIndex, rowCount);
            fingerprint = PublishedReadFingerprint.AddValue(fingerprint, ReadValue(array, kind, position));
        }
        return new PublishedReadResult(rowCount, fingerprint);
    }

    static object? ReadValue(IArrowArray array, BenchmarkColumnKind kind, int index)
        => kind switch
        {
            BenchmarkColumnKind.Boolean => ((BooleanArray)array).GetValue(index),
            BenchmarkColumnKind.Int32 => ((Int32Array)array).GetValue(index),
            BenchmarkColumnKind.Int64 => ((Int64Array)array).GetValue(index),
            BenchmarkColumnKind.Timestamp => ((TimestampArray)array).GetTimestamp(index),
            BenchmarkColumnKind.Double => ((DoubleArray)array).GetValue(index),
            BenchmarkColumnKind.String => ((StringArray)array).GetString(index),
            _ => throw new NotSupportedException($"Unsupported column kind '{kind}'.")
        };
}
