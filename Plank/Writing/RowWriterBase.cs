using System.Runtime.ExceptionServices;
using Plank.Schema;

namespace Plank.Writing;

public abstract class RowWriterBase<TSlot>
    where TSlot : class, IRowWriterSlot
{
    readonly ParquetWriter _writer;
    readonly Queue<QueuedSlot> _readySlots;
    readonly Queue<TSlot> _freeSlots;
    readonly Thread[] _workers;
    readonly object _gate;
    readonly object _writeGate;
    bool _initialSlotTaken;
    bool _slotsInitialized;
    ulong _nextQueuedSequence;
    ulong _nextWriteSequence;
    bool _addingCompleted;
    bool _completed;
    ExceptionDispatchInfo? _fault;

    protected RowWriterBase(Stream stream, ParquetSchema schema, uint maxParallelism, ParquetWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        if (maxParallelism == 0)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism), maxParallelism,
                "Max parallelism must be greater than zero.");
        if (maxParallelism > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism), maxParallelism,
                $"Max parallelism must be <= {int.MaxValue}.");

        _writer = ParquetWriter.Create(stream, schema, options);
        var workerCount = checked((int)maxParallelism);
        _readySlots = new Queue<QueuedSlot>(workerCount);
        _freeSlots = new Queue<TSlot>(workerCount);
        _workers = new Thread[workerCount];
        _gate = new object();
        _writeGate = new object();
        _initialSlotTaken = false;
        _slotsInitialized = false;
        _nextQueuedSequence = 0;
        _nextWriteSequence = 0;
        _addingCompleted = false;
        _completed = false;
        _fault = null;
    }

    protected abstract TSlot CreateSlot(ParquetWriter writer);

    protected void InitializeSlots()
    {
        lock (_gate)
        {
            if (_slotsInitialized)
                throw new InvalidOperationException("Row writer slots are already initialized.");

            for (var i = 0; i < _workers.Length; i++)
                _freeSlots.Enqueue(CreateSlotChecked());

            for (var i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = $"PlankRowApiWorker-{i}"
                };
                _workers[i].Start();
            }

            _slotsInitialized = true;
        }
    }

    protected TSlot TakeInitialSlot()
    {
        lock (_gate)
        {
            ThrowIfNotInitialized();
            ThrowIfFaulted();
            if (_completed)
                throw new InvalidOperationException("Row writer is already completed.");
            if (_initialSlotTaken)
                throw new InvalidOperationException("Initial slot was already taken.");

            _initialSlotTaken = true;
            return TakeFreeSlotNoLock();
        }
    }

    protected TSlot EnqueueAndTakeFree(TSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        lock (_gate)
        {
            ThrowIfNotInitialized();
            ThrowIfFaulted();
            if (_completed)
                throw new InvalidOperationException("Row writer is already completed.");

            EnqueueReadySlotNoLock(slot);
            return TakeFreeSlotNoLock();
        }
    }

    protected void Complete(TSlot activeSlot, bool hasRows)
    {
        ArgumentNullException.ThrowIfNull(activeSlot);

        lock (_gate)
        {
            ThrowIfNotInitialized();
            if (!_completed)
            {
                if (hasRows && _fault is null)
                    EnqueueReadySlotNoLock(activeSlot);

                _completed = true;
                _addingCompleted = true;
                Monitor.PulseAll(_gate);
            }
        }

        for (var i = 0; i < _workers.Length; i++)
            _workers[i].Join();

        ThrowIfFaulted();
        _writer.CloseFile();
    }

    protected void ThrowIfFaulted()
    {
        var fault = _fault;
        if (fault is not null)
            fault.Throw();
    }

    void ThrowIfNotInitialized()
    {
        if (!_slotsInitialized)
            throw new InvalidOperationException("Row writer slots are not initialized. Call InitializeSlots() first.");
    }

    TSlot CreateSlotChecked()
    {
        var slot = CreateSlot(_writer);
        ArgumentNullException.ThrowIfNull(slot);
        return slot;
    }

    void EnqueueReadySlotNoLock(TSlot slot)
    {
        _readySlots.Enqueue(new QueuedSlot(slot, _nextQueuedSequence++));
        Monitor.PulseAll(_gate);
    }

    TSlot TakeFreeSlotNoLock()
    {
        while (_freeSlots.Count == 0)
        {
            ThrowIfFaulted();
            Monitor.Wait(_gate);
        }

        return _freeSlots.Dequeue();
    }

    void WorkerLoop()
    {
        while (true)
        {
            QueuedSlot queuedSlot;
            lock (_gate)
            {
                while (_readySlots.Count == 0 && !_addingCompleted && _fault is null)
                    Monitor.Wait(_gate);

                if (_readySlots.Count == 0)
                    return;

                queuedSlot = _readySlots.Dequeue();
            }

            try
            {
                queuedSlot.Slot.SerializeColumns();
                lock (_writeGate)
                {
                    while (queuedSlot.Sequence != _nextWriteSequence && _fault is null)
                        Monitor.Wait(_writeGate);

                    ThrowIfFaulted();
                    var rowGroupWriter = _writer.StartRowGroup();
                    queuedSlot.Slot.WriteSerialized(rowGroupWriter);
                    _nextWriteSequence++;
                    Monitor.PulseAll(_writeGate);
                }
            }
            catch (Exception ex)
            {
                RecordFault(ex);
            }
            finally
            {
                queuedSlot.Slot.ResetForReuse();
                lock (_gate)
                {
                    _freeSlots.Enqueue(queuedSlot.Slot);
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    void RecordFault(Exception exception)
    {
        var captured = ExceptionDispatchInfo.Capture(exception);
        if (Interlocked.CompareExchange(ref _fault, captured, null) is not null)
            return;

        lock (_gate)
        {
            _addingCompleted = true;
            Monitor.PulseAll(_gate);
        }

        lock (_writeGate)
            Monitor.PulseAll(_writeGate);
    }

    readonly struct QueuedSlot(TSlot slot, ulong sequence)
    {
        internal TSlot Slot { get; } = slot;
        internal ulong Sequence { get; } = sequence;
    }
}
