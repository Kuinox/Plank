using System.Collections.Concurrent;
using Plank.Reading;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class RowReaderParallelismTests
{
    const int RowsPerGroup = 4097;

    [Test]
    [Arguments(1, 0)]
    [Arguments(1, 1)]
    [Arguments(4, 0)]
    [Arguments(4, 1)]
    public void OrderedBuffersProjectionAndReset(int workers, int readAhead)
    {
        var bytes = CreateFile();
        var starts = new ConcurrentDictionary<int, byte>();
        using var source = new TrackingSource(bytes);
        using var reader = EncodedRowSchema.CreateRowReader(source, options: new RowReaderOptions
        {
            Execution = new() { WorkerCount = workers, OnWorkerStarted = _ => starts.TryAdd(Environment.CurrentManagedThreadId, 0) },
            MaxReadAheadRowGroups = readAhead
        });
        CheckRows(reader);
        if (workers == 1 && starts.Count != 0 || workers > 1 && starts.Count != 4)
            throw new InvalidOperationException($"Unexpected worker count: {starts.Count}.");
        if (workers > 1 && !source.ReadThreads.Keys.Any(starts.ContainsKey))
            throw new InvalidOperationException("No worker read column data.");

        reader.Reset(source, EncodedRowSchema.Projection.Id | EncodedRowSchema.Projection.DefaultValue);
        var index = 0;
        while (reader.MoveNext())
        {
            if (reader.Current.Id != (ulong)index || reader.Current.DefaultValue != (uint)(index * 7))
                throw new InvalidOperationException($"Incorrect projected row {index}.");
            index++;
        }
        if (index != RowsPerGroup * 3)
            throw new InvalidOperationException("Incorrect projected row count.");
        reader.Reset(source);
        if (!reader.MoveNext()) throw new InvalidOperationException("Reset did not return a row.");
        // Reset with pending read-ahead, then use the stream overload and read to completion.
        using var stream = new MemoryStream(bytes);
        reader.Reset(stream);
        CheckRows(reader);
        if (source.Disposed) throw new InvalidOperationException("Reader disposed a caller-owned source.");
        if (workers > 1 && starts.Count != 4)
            throw new InvalidOperationException("Reset unnecessarily restarted workers.");
    }

    [Test]
    public void WorkersActuallyDecodeConcurrently()
    {
        using var pool = new ConcurrentPool();
        using var source = new MemoryReadSource(CreateFile());
        using var reader = EncodedRowSchema.CreateRowReader(source, options: new RowReaderOptions
        {
            BufferPool = pool,
            Execution = new() { WorkerCount = 2 },
            MaxReadAheadRowGroups = 0
        });
        CheckRows(reader);
        if (pool.Threads.Count != 2)
            throw new InvalidOperationException("Expected two concurrent decoder workers.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public void ReadAheadIsBoundedAndDisposeJoinsIt(int readAhead)
    {
        var bytes = CreateFile();
        using var metadataSource = new MemoryReadSource(bytes);
        using var metadata = EncodedRowSchema.CreateReader(metadataSource);
        var second = metadata.RowGroups[1].ColumnChunkOffset;
        var third = metadata.RowGroups[2].ColumnChunkOffset;
        using var source = new TrackingSource(bytes) { NextGroup = second, ThirdGroup = third };
        var reader = EncodedRowSchema.CreateRowReader(source, options: new RowReaderOptions
        {
            Execution = new() { WorkerCount = 2 }, MaxReadAheadRowGroups = readAhead
        });
        source.TrackGroups = true;
        try
        {
            if (!reader.MoveNext()) throw new InvalidOperationException("Missing first row.");
            if (readAhead == 1 && !source.NextGroupRead.Wait(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("The next row group was not prefetched.");
        }
        finally { reader.Dispose(); }
        if (source.ThirdGroupRead || source.NextGroupRead.IsSet != (readAhead == 1))
            throw new InvalidOperationException("Read-ahead exceeded its configured bound.");
        source.Disposed = true; // Any late background source access now fails.
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public void ReadAheadFailureAppearsAtItsRowGroupAndResetRecovers(int readAhead)
    {
        var bytes = CreateFile();
        using var metadataSource = new MemoryReadSource(bytes);
        using var metadata = EncodedRowSchema.CreateReader(metadataSource);
        using var source = new TrackingSource(bytes) { NextGroup = metadata.RowGroups[1].ColumnChunkOffset };
        using var reader = EncodedRowSchema.CreateRowReader(source, options: new RowReaderOptions
        {
            Execution = new() { WorkerCount = 2 }, MaxReadAheadRowGroups = readAhead
        });
        source.FailNextGroup = true;
        for (var i = 0; i < RowsPerGroup; i++)
            if (!reader.MoveNext() || reader.Current.Id != (ulong)i)
                throw new InvalidOperationException("Read-ahead fault affected an earlier row group.");
        var previous = reader.Current;
        ExpectFailure(() => reader.MoveNext());
        var unavailable = false;
        try { _ = previous.Id; }
        catch (InvalidOperationException) { unavailable = true; }
        if (!unavailable)
            throw new InvalidOperationException("A failed refill left the previous values accessible.");
        ExpectFailure(() => reader.MoveNext());
        source.FailNextGroup = false;
        reader.Reset(source);
        CheckRows(reader);
    }

    [Test]
    public void WorkerStartupFailureIsPropagated()
    {
        using var source = new MemoryReadSource(CreateFile());
        using var reader = EncodedRowSchema.CreateRowReader(source, options: new RowReaderOptions
        {
            Execution = new() { WorkerCount = 2, OnWorkerStarted = _ => throw new ProbeException() }
        });
        ExpectFailure(() => reader.MoveNext());
    }

    static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (ProbeException) { return; }
        throw new InvalidOperationException("Expected the original worker exception.");
    }

    static void CheckRows(EncodedRowSchema.RowReader reader)
    {
        var index = 0;
        while (reader.MoveNext())
        {
            var row = reader.Current;
            if (row.Id != (ulong)index || row.DefaultValue != (uint)(index * 7) ||
                !row.Payload.Value.SequenceEqual(BitConverter.GetBytes(index)) ||
                row.Tag.IsNull != (index % 5 == 0) ||
                !row.Tag.IsNull && !row.Tag.Value.SequenceEqual(new byte[] { (byte)(index % 13) }))
                throw new InvalidOperationException($"Incorrect row {index}.");
            index++;
        }
        if (index != RowsPerGroup * 3) throw new InvalidOperationException($"Incorrect row count {index}.");
    }

    static byte[] CreateFile()
    {
        using var stream = new MemoryStream();
        using var writer = EncodedRowSchema.CreateWriter(stream, new ParquetWriterOptions { TargetDataPageSizeBytes = 1024 });
        for (var group = 0; group < 3; group++)
        {
            var indices = Enumerable.Range(group * RowsPerGroup, RowsPerGroup).ToArray();
            var rowGroup = writer.StartRowGroup();
            rowGroup.Id.Serialize(indices.Select(i => (ulong)i).ToArray());
            rowGroup.Write(rowGroup.Id);
            rowGroup.Tag.Serialize(indices.Select(i => i % 5 == 0 ? null : new byte[] { (byte)(i % 13) }).ToArray());
            rowGroup.Write(rowGroup.Tag);
            rowGroup.Payload.Serialize(indices.Select(BitConverter.GetBytes).ToArray());
            rowGroup.Write(rowGroup.Payload);
            rowGroup.DefaultValue.Serialize(indices.Select(i => (uint)(i * 7)).ToArray());
            rowGroup.Write(rowGroup.DefaultValue);
        }
        writer.CloseFile();
        return stream.ToArray();
    }

    sealed class ProbeException : Exception;

    sealed class TrackingSource(byte[] bytes) : IParquetReadSource
    {
        int _reading;
        internal readonly ConcurrentDictionary<int, byte> ReadThreads = new();
        internal readonly ManualResetEventSlim NextGroupRead = new();
        internal ulong NextGroup = ulong.MaxValue;
        internal ulong ThirdGroup = ulong.MaxValue;
        internal bool TrackGroups, ThirdGroupRead, FailNextGroup, Disposed;
        public ulong Length => (ulong)bytes.Length;
        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            if (Disposed || Interlocked.Exchange(ref _reading, 1) != 0)
                throw new InvalidOperationException("Concurrent or late access to a non-thread-safe source.");
            try
            {
                ReadThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
                if (FailNextGroup && offset >= NextGroup) throw new ProbeException();
                if (TrackGroups && offset >= NextGroup) NextGroupRead.Set();
                if (TrackGroups && offset >= ThirdGroup) ThirdGroupRead = true;
                bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
            }
            finally { Volatile.Write(ref _reading, 0); }
        }
        public void Dispose() { Disposed = true; NextGroupRead.Dispose(); }
    }

    sealed class ConcurrentPool : IParquetBufferPool, IDisposable
    {
        readonly CountdownEvent _entered = new(2);
        internal readonly ConcurrentDictionary<int, byte> Threads = new();
        public ParquetBuffer Rent(uint minimumByteLength)
        {
            if (Thread.CurrentThread.Name?.StartsWith("Plank-RowReader-", StringComparison.Ordinal) == true &&
                Threads.TryAdd(Environment.CurrentManagedThreadId, 0))
            {
                _entered.Signal();
                if (!_entered.Wait(TimeSpan.FromSeconds(10)))
                    throw new InvalidOperationException("Column decoding did not execute concurrently.");
            }
            return DefaultParquetBufferPool.Shared.Rent(minimumByteLength);
        }
        public void Dispose() => _entered.Dispose();
    }
}
