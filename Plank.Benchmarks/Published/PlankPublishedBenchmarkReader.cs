using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Benchmarks.Published;

sealed class PlankPublishedBenchmarkReader : IPublishedBenchmarkReader
{
    readonly PublishedBenchmarkDataSet _dataSet;
    readonly MemoryReadSource _source;
    readonly int _workerCount;
    readonly DedicatedColumnWorker[]? _dedicatedWorkers;

    public PlankPublishedBenchmarkReader(byte[] fileBytes, PublishedBenchmarkDataSet dataSet, int workerCount)
    {
        _dataSet = dataSet;
        _source = new MemoryReadSource(fileBytes);
        _workerCount = workerCount;
        if (workerCount > 1 && dataSet.Columns.Count is 2 or 3)
            _dedicatedWorkers = Enumerable.Range(1, dataSet.Columns.Count - 1)
                .Select(static columnIndex => new DedicatedColumnWorker(columnIndex))
                .ToArray();
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
            else if (pieces.Length is 2 or 3)
                ReadFewColumns(rowGroup, rowCount, pieces, cancellationToken);
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

    void ReadFewColumns(RowGroup rowGroup, int rowCount, PublishedReadResult[] pieces,
        CancellationToken cancellationToken)
    {
        var workers = _dedicatedWorkers ?? throw new InvalidOperationException(
            "Dedicated column workers were not initialized.");
        for (var columnIndex = 1; columnIndex < pieces.Length; columnIndex++)
        {
            var capturedColumnIndex = columnIndex;
            workers[columnIndex - 1].Start(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                pieces[capturedColumnIndex] = ReadColumn(rowGroup, capturedColumnIndex, rowCount);
            });
        }

        var failures = new Exception?[workers.Length];
        try
        {
            pieces[0] = ReadColumn(rowGroup, 0, rowCount);
        }
        finally
        {
            for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
                failures[workerIndex] = workers[workerIndex].Join();
        }

        var workerFailures = failures.OfType<Exception>().ToArray();
        if (workerFailures.Length != 0)
            throw new AggregateException("A published benchmark column failed to read.", workerFailures);
    }

    public void Dispose()
    {
        if (_dedicatedWorkers is not null)
            foreach (var worker in _dedicatedWorkers)
                worker.Dispose();
    }

    sealed class DedicatedColumnWorker : IDisposable
    {
        readonly AutoResetEvent _ready = new(false);
        readonly AutoResetEvent _done = new(false);
        readonly Thread _thread;
        Action? _work;
        Exception? _failure;
        bool _stopping;

        internal DedicatedColumnWorker(int columnIndex)
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"Plank published column reader {columnIndex}"
            };
            _thread.Start();
        }

        internal void Start(Action work)
        {
            _work = work ?? throw new ArgumentNullException(nameof(work));
            _failure = null;
            _ready.Set();
        }

        internal Exception? Join()
        {
            _done.WaitOne();
            return _failure;
        }

        public void Dispose()
        {
            _stopping = true;
            _ready.Set();
            _thread.Join();
            _ready.Dispose();
            _done.Dispose();
        }

        void Run()
        {
            while (true)
            {
                _ready.WaitOne();
                if (_stopping)
                    return;
                try
                {
                    _work!();
                }
                catch (Exception exception)
                {
                    _failure = exception;
                }
                finally
                {
                    _work = null;
                    _done.Set();
                }
            }
        }
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
            fingerprint.AddValues(values);
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
