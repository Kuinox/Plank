using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Benchmarks.Published;

sealed class PlankPublishedBenchmarkReader : IPublishedBenchmarkReader
{
    readonly PublishedBenchmarkDataSet _dataSet;
    readonly MemoryReadSource _source;
    readonly int _workerCount;

    public PlankPublishedBenchmarkReader(byte[] fileBytes, PublishedBenchmarkDataSet dataSet, int workerCount)
    {
        _dataSet = dataSet;
        _source = new MemoryReadSource(fileBytes);
        _workerCount = workerCount;
    }

    public string ImplementationId
        => _workerCount == 1 ? "plank-single" : "plank-multi";

    public string Label
        => _workerCount == 1 ? "Plank (1 thread)" : $"Plank ({_workerCount} threads)";

    public int Threads
        => _workerCount;

    public bool IsSupported
        => true;

    public string? UnavailableReason
        => null;

    public ValueTask<PublishedReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new ParquetReader();
        reader.Reset(_source);
        var aggregate = PublishedReadFingerprint.Start();
        long valueCount = 0;
        foreach (var rowGroup in reader.RowGroups)
        {
            var rowCount = checked((int)rowGroup.RowCount);
            var pieces = new PublishedReadResult[_dataSet.Columns.Count];
            if (_workerCount == 1)
                for (var columnIndex = 0; columnIndex < pieces.Length; columnIndex++)
                    pieces[columnIndex] = ReadColumn(rowGroup, columnIndex, rowCount);
            else
                Parallel.For(0, pieces.Length, new ParallelOptions
                {
                    MaxDegreeOfParallelism = _workerCount,
                    CancellationToken = cancellationToken
                }, columnIndex => pieces[columnIndex] = ReadColumn(rowGroup, columnIndex, rowCount));

            for (var columnIndex = 0; columnIndex < pieces.Length; columnIndex++)
            {
                aggregate = PublishedReadFingerprint.Combine(aggregate, pieces[columnIndex]);
                valueCount = checked(valueCount + pieces[columnIndex].ValueCount);
            }
        }
        return ValueTask.FromResult(new PublishedReadResult(valueCount, aggregate));
    }

    public void Dispose()
    {
    }

    PublishedReadResult ReadColumn(RowGroup rowGroup, int columnIndex, int rowCount)
        => (_dataSet.Columns[columnIndex].Kind, _dataSet.Columns[columnIndex].Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, true) => ReadFixed<bool?>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Boolean, false) => ReadFixed<bool>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Int32, true) => ReadFixed<int?>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Int32, false) => ReadFixed<int>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Int64, true) => ReadFixed<long?>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Int64, false) => ReadFixed<long>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Timestamp, true) => ReadFixed<DateTime?>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Timestamp, false) => ReadFixed<DateTime>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Double, true) => ReadFixed<double?>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.Double, false) => ReadFixed<double>(rowGroup, columnIndex, rowCount),
            (BenchmarkColumnKind.String, _) => ReadBinary(rowGroup, columnIndex, rowCount),
            _ => throw new NotSupportedException(
                $"Unsupported column kind '{_dataSet.Columns[columnIndex].Kind}'.")
        };

    static PublishedReadResult ReadFixed<T>(RowGroup rowGroup, int columnIndex, int rowCount)
    {
        var fingerprint = PublishedReadFingerprint.Accumulator.StartPiece(columnIndex, rowGroup.Index, rowCount);
        var offset = 0;
        foreach (var buffer in rowGroup.Column<T>(columnIndex))
        {
            var values = buffer.Values;
            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
                fingerprint.AddValue(values[valueIndex]);
            offset = checked(offset + buffer.Count);
        }
        ValidateCount(columnIndex, rowCount, offset);
        return new PublishedReadResult(offset, fingerprint.Finish());
    }

    static PublishedReadResult ReadBinary(RowGroup rowGroup, int columnIndex, int rowCount)
    {
        var fingerprint = PublishedReadFingerprint.Accumulator.StartPiece(columnIndex, rowGroup.Index, rowCount);
        var offset = 0;
        foreach (var buffer in rowGroup.Column<byte>(columnIndex))
        {
            for (var localIndex = 0; localIndex < buffer.Count; localIndex++)
                if (buffer.IsNull(localIndex))
                    fingerprint.AddNull();
                else
                    fingerprint.AddBytes(buffer.GetValue(localIndex));
            offset = checked(offset + buffer.Count);
        }
        ValidateCount(columnIndex, rowCount, offset);
        return new PublishedReadResult(offset, fingerprint.Finish());
    }

    static void ValidateCount(int columnIndex, int expected, int actual)
    {
        if (actual != expected)
            throw new InvalidDataException(
                $"Column {columnIndex} decoded {actual} values instead of {expected}.");
    }
}
