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
            var pieces = new PublishedReadResult[batch.ColumnCount];
            if (_useThreads)
                Parallel.For(0, batch.ColumnCount, new ParallelOptions
                {
                    MaxDegreeOfParallelism = _workerCount,
                    CancellationToken = cancellationToken
                }, columnIndex => pieces[columnIndex] = ReadColumn(batch.Column(columnIndex),
                    _dataSet.Columns[columnIndex].Kind, columnIndex, rowGroupIndex, batch.Length));
            else
                for (var columnIndex = 0; columnIndex < batch.ColumnCount; columnIndex++)
                    pieces[columnIndex] = ReadColumn(batch.Column(columnIndex),
                        _dataSet.Columns[columnIndex].Kind, columnIndex, rowGroupIndex, batch.Length);

            for (var columnIndex = 0; columnIndex < pieces.Length; columnIndex++)
            {
                aggregate = PublishedReadFingerprint.Combine(aggregate, pieces[columnIndex]);
                valueCount = checked(valueCount + pieces[columnIndex].ValueCount);
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
        return kind switch
        {
            BenchmarkColumnKind.Boolean => Consume((BooleanArray)array, columnIndex, rowGroupIndex),
            BenchmarkColumnKind.Int32 => Consume((Int32Array)array, columnIndex, rowGroupIndex),
            BenchmarkColumnKind.Int64 => Consume((Int64Array)array, columnIndex, rowGroupIndex),
            BenchmarkColumnKind.Timestamp => Consume((TimestampArray)array, columnIndex, rowGroupIndex),
            BenchmarkColumnKind.Double => Consume((DoubleArray)array, columnIndex, rowGroupIndex),
            BenchmarkColumnKind.String => Consume((StringArray)array, columnIndex, rowGroupIndex),
            _ => throw new NotSupportedException($"Unsupported column kind '{kind}'.")
        };
    }

    static PublishedReadResult Consume<T>(PrimitiveArray<T> array, int columnIndex, int rowGroupIndex)
        where T : struct, IEquatable<T>
    {
        var fingerprint = PublishedReadFingerprint.Accumulator.StartPiece(columnIndex, rowGroupIndex, array.Length);
        if (array.NullCount == 0)
        {
            fingerprint.AddValues(array.Values);
        }
        else
        {
            for (var valueIndex = 0; valueIndex < array.Length; valueIndex++)
                fingerprint.AddValue(array.GetValue(valueIndex));
        }
        return new PublishedReadResult(array.Length, fingerprint.Finish());
    }

    static PublishedReadResult Consume(BooleanArray array, int columnIndex, int rowGroupIndex)
    {
        var fingerprint = PublishedReadFingerprint.Accumulator.StartPiece(columnIndex, rowGroupIndex, array.Length);
        for (var valueIndex = 0; valueIndex < array.Length; valueIndex++)
            fingerprint.AddValue(array.GetValue(valueIndex));
        return new PublishedReadResult(array.Length, fingerprint.Finish());
    }

    static PublishedReadResult Consume(TimestampArray array, int columnIndex, int rowGroupIndex)
    {
        var fingerprint = PublishedReadFingerprint.Accumulator.StartPiece(columnIndex, rowGroupIndex, array.Length);
        for (var valueIndex = 0; valueIndex < array.Length; valueIndex++)
            fingerprint.AddValue(array.GetTimestamp(valueIndex));
        return new PublishedReadResult(array.Length, fingerprint.Finish());
    }

    static PublishedReadResult Consume(StringArray array, int columnIndex, int rowGroupIndex)
    {
        var fingerprint = PublishedReadFingerprint.Accumulator.StartPiece(columnIndex, rowGroupIndex, array.Length);
        for (var valueIndex = 0; valueIndex < array.Length; valueIndex++)
        {
            var bytes = array.GetBytes(valueIndex, out var isNull);
            if (isNull)
                fingerprint.AddNull();
            else
                fingerprint.AddBytes(bytes);
        }
        return new PublishedReadResult(array.Length, fingerprint.Finish());
    }
}
