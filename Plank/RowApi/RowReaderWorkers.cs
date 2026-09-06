using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.RowApi;

// Jobs own distinct column states. The caller waits before exposing or reusing them.
sealed class RowReaderWorkers : IDisposable
{
    readonly BlockingCollection<Work> _queue = new();
    readonly Thread[] _threads;
    readonly ParquetExecutionOptions _execution;
    ExceptionDispatchInfo? _startupFault;
    readonly CountdownEvent _started;

    internal RowReaderWorkers(ParquetExecutionOptions execution, int count)
    {
        _execution = execution;
        _started = new CountdownEvent(count);
        _threads = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            var index = i;
            _threads[i] = new Thread(() => Run(index))
            {
                IsBackground = true,
                Name = $"Plank-RowReader-{i}"
            };
        }
        var started = 0;
        try
        {
            for (; started < count; started++)
                _threads[started].Start();
        }
        catch
        {
            _queue.CompleteAdding();
            for (var i = 0; i < started; i++)
                _threads[i].Join();
            _queue.Dispose();
            _started.Dispose();
            throw;
        }
        _started.Wait();
        if (_startupFault is not null)
        {
            Dispose();
            _startupFault.Throw();
        }
    }

    internal int Count => _threads.Length;

    internal void Enqueue(Work work)
    {
        work.Done.Reset();
        _queue.Add(work);
    }

    void Run(int index)
    {
        try
        {
            _execution.OnWorkerStarted?.Invoke(new ParquetWorkerContext(index, _threads.Length,
                Thread.CurrentThread.Name!));
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _startupFault, ExceptionDispatchInfo.Capture(ex), null);
        }
        finally
        {
            _started.Signal();
        }
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                Volatile.Read(ref _startupFault)?.Throw();
                work.Execute();
            }
            catch (Exception ex)
            {
                work.Fault = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                work.Done.Set();
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var thread in _threads)
            thread.Join();
        _queue.Dispose();
        _started.Dispose();
    }

    internal sealed class Work(RowApiColumnReadState state) : IDisposable
    {
        internal readonly RowApiColumnReadState State = state;
        internal readonly ManualResetEventSlim Done = new(true);
        internal ExceptionDispatchInfo? Fault;
        internal RowGroup? PrefetchGroup;

        internal void Execute()
        {
            if (PrefetchGroup is { } group)
            {
                State.Open(group);
                State.AdvanceBuffer();
                State.Prefetched = true;
                State.CurrentIndex = -1;
            }
            else
                State.TakeNextBuffer();
        }

        public void Dispose() => Done.Dispose();
    }
}

// The public source contract does not require concurrent reads. Keep arbitrary
// sources compatible while allowing decompression and decoding outside this lock.
sealed class RowReaderSynchronizedSource(IParquetReadSource source) : IParquetReadSource
{
    readonly object _gate = new();
    public ulong Length
    {
        get
        {
            lock (_gate)
                return source.Length;
        }
    }
    public void ReadExactly(ulong offset, Span<byte> destination)
    {
        lock (_gate)
            source.ReadExactly(offset, destination);
    }
    public void Dispose() { }
}
