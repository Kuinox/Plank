using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Plank.Schema;

namespace Plank.Writing;

/// <summary>Provides the parallel serialization pipeline used by generated row writers.</summary>
/// <typeparam name="TSlot">The generated row-buffer slot type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public abstract class RowWriterBase<TSlot> : IDisposable
    where TSlot : class
{
    readonly ParquetWriter _writer;
    readonly IParquetWriteSource? _rollingFile;
    readonly ParquetFilePath? _filePath;
    readonly IParquetBufferPool _bufferPool;
    readonly ulong _targetFileSizeBytes;
    // Slots own reusable input and serialized buffers. Pinning each slot to one worker avoids migrating those hot
    // buffers between cores (and potentially CCDs); the owner map routes a slot back after the producer returns it.
    readonly Queue<QueuedSlot>[] _workerReadySlots;
    readonly Queue<TSlot> _freeSlots;
    readonly Dictionary<TSlot, int> _slotOwners;
    readonly Dictionary<ulong, QueuedSlot> _serializedSlots;
    readonly Thread[] _workers;
    readonly TSlot?[] _slots;
    readonly ParquetExecutionOptions _execution;
    readonly object _gate;
    readonly object _writeGate;
    readonly SemaphoreSlim[] _workerReadySignals;
    readonly SemaphoreSlim _freeSignal;
    bool _initialSlotTaken;
    bool _slotsInitialized;
    ulong _nextQueuedSequence;
    ulong _nextWriteSequence;
    bool _writerActive;
    bool _addingCompleted;
    bool _completed;
    ulong _fileIndex;
    bool _rolloverPending;
    bool _disposed;
    ExceptionDispatchInfo? _fault;

    /// <summary>Initializes the pipeline used by a generated row writer.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="schema">The generated Parquet schema.</param>
    /// <param name="maxParallelism">The maximum number of serialization workers.</param>
    /// <param name="options">The Parquet writer options.</param>
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

        _writer = schema.CreateWriter(stream, options);
        _rollingFile = null;
        _filePath = null;
        _bufferPool = options.BufferPool;
        _targetFileSizeBytes = options.TargetFileSizeBytes;
        var workerCount = checked((int)maxParallelism);
        _execution = options.Execution;
        _workerReadySlots = CreateWorkerReadySlots(workerCount);
        _freeSlots = new Queue<TSlot>(workerCount);
        _slotOwners = new Dictionary<TSlot, int>(workerCount, ReferenceEqualityComparer.Instance);
        _serializedSlots = new Dictionary<ulong, QueuedSlot>(workerCount);
        _workers = new Thread[workerCount];
        _slots = new TSlot?[workerCount];
        _gate = new object();
        _writeGate = new object();
        _workerReadySignals = CreateWorkerReadySignals(workerCount);
        _freeSignal = new SemaphoreSlim(0);
        _initialSlotTaken = false;
        _slotsInitialized = false;
        _nextQueuedSequence = 0;
        _nextWriteSequence = 0;
        _writerActive = false;
        _addingCompleted = false;
        _completed = false;
        _fileIndex = 0;
        _rolloverPending = false;
        _disposed = false;
        _fault = null;
    }

    /// <summary>Initializes a rolling pipeline used by a generated row writer.</summary>
    /// <param name="file">The reusable destination used for each produced file.</param>
    /// <param name="filePath">Selects the path of each produced file.</param>
    /// <param name="schema">The generated Parquet schema.</param>
    /// <param name="maxParallelism">The maximum number of serialization workers.</param>
    /// <param name="options">The Parquet writer options.</param>
    protected RowWriterBase(IParquetWriteSource file, ParquetFilePath filePath, ParquetSchema schema,
        uint maxParallelism, ParquetWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        if (maxParallelism == 0)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism), maxParallelism,
                "Max parallelism must be greater than zero.");
        if (maxParallelism > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism), maxParallelism,
                $"Max parallelism must be <= {int.MaxValue}.");

        options.Validate();
        OpenRollingFile(file, filePath, 0, options.BufferPool);
        try
        {
            _writer = schema.CreateWriter(file, options);
        }
        catch
        {
            file.Close();
            throw;
        }

        _rollingFile = file;
        _filePath = filePath;
        _bufferPool = options.BufferPool;
        _targetFileSizeBytes = options.TargetFileSizeBytes;
        var workerCount = checked((int)maxParallelism);
        _execution = options.Execution;
        _workerReadySlots = CreateWorkerReadySlots(workerCount);
        _freeSlots = new Queue<TSlot>(workerCount);
        _slotOwners = new Dictionary<TSlot, int>(workerCount, ReferenceEqualityComparer.Instance);
        _serializedSlots = new Dictionary<ulong, QueuedSlot>(workerCount);
        _workers = new Thread[workerCount];
        _slots = new TSlot?[workerCount];
        _gate = new object();
        _writeGate = new object();
        _workerReadySignals = CreateWorkerReadySignals(workerCount);
        _freeSignal = new SemaphoreSlim(0);
        _initialSlotTaken = false;
        _slotsInitialized = false;
        _nextQueuedSequence = 0;
        _nextWriteSequence = 0;
        _writerActive = false;
        _addingCompleted = false;
        _completed = false;
        _fileIndex = 0;
        _rolloverPending = false;
        _disposed = false;
        _fault = null;
    }

    /// <summary>Creates a generated row-buffer slot.</summary>
    /// <param name="writer">The destination Parquet writer.</param>
    /// <returns>A new buffer slot.</returns>
    protected abstract TSlot CreateSlot(ParquetWriter writer);
    /// <summary>Serializes a generated row-buffer slot.</summary>
    /// <param name="slot">The slot to serialize.</param>
    protected abstract void SerializeSlot(TSlot slot);
    /// <summary>Writes a serialized slot to a row group.</summary>
    /// <param name="slot">The serialized slot.</param>
    /// <param name="rowGroupWriter">The destination row-group writer.</param>
    protected abstract void WriteSerializedSlot(TSlot slot, RowGroupWriter rowGroupWriter);
    /// <summary>Resets a generated slot before it is reused.</summary>
    /// <param name="slot">The slot to reset.</param>
    protected abstract void ResetSlotForReuse(TSlot slot);
    /// <summary>Gets the name prefix used for serialization worker threads.</summary>
    protected virtual string WorkerThreadNamePrefix
        => "PlankRowApiWorker";

    /// <summary>Handles successful writing of a generated slot.</summary>
    /// <param name="slot">The slot that was written.</param>
    protected virtual void OnSlotWritten(TSlot slot)
    {
    }

    /// <summary>Creates the generated slots and starts serialization workers.</summary>
    protected void InitializeSlots()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_slotsInitialized)
                throw new InvalidOperationException("Row writer slots are already initialized.");

            for (var i = 0; i < _workers.Length; i++)
            {
                var slot = CreateSlotChecked();
                _slots[i] = slot;
                // Keep the slot's reusable input and serialized buffers on the same worker across every reuse.
                _slotOwners.Add(slot, i);
                _freeSlots.Enqueue(slot);
                _freeSignal.Release();
            }

            StartWorkersNoLock();

            _slotsInitialized = true;
        }
    }

    /// <summary>Resets a completed generated row-writer pipeline to a new destination stream.</summary>
    /// <param name="stream">The new destination stream.</param>
    protected void ResetPipeline(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfNotInitialized();
            if (_rollingFile is not null)
                throw new InvalidOperationException("A rolling row writer cannot be reset to a stream.");
            if (!_completed)
                throw new InvalidOperationException("The row writer must be completed before it can be reset.");
            RethrowFault();
            if (_freeSlots.Count != _slots.Length)
                throw new InvalidOperationException("The completed row writer did not return every buffer slot.");

            _writer.Reset(stream);
            _initialSlotTaken = false;
            _nextQueuedSequence = 0;
            _nextWriteSequence = 0;
            _writerActive = false;
            _addingCompleted = false;
            _completed = false;
            _fileIndex = 0;
            _rolloverPending = false;
            StartWorkersNoLock();
        }
    }

    /// <summary>Takes the first slot used by a generated writer.</summary>
    /// <returns>The first writable slot.</returns>
    protected TSlot TakeInitialSlot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfNotInitialized();
            ThrowIfFaulted();
            if (_completed)
                throw new InvalidOperationException("Row writer is already completed.");
            if (_initialSlotTaken)
                throw new InvalidOperationException("Initial slot was already taken.");

            _initialSlotTaken = true;
        }

        return TakeFreeSlot();
    }

    /// <summary>Queues a filled slot and obtains the next writable slot.</summary>
    /// <param name="slot">The filled slot to queue.</param>
    /// <returns>The next writable slot.</returns>
    protected TSlot EnqueueAndTakeFree(TSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfNotInitialized();
            ThrowIfFaulted();
            if (_completed)
                throw new InvalidOperationException("Row writer is already completed.");

            EnqueueReadySlotNoLock(slot);
        }

        return TakeFreeSlot();
    }

    /// <summary>Completes the serialization pipeline and closes the Parquet file.</summary>
    /// <param name="activeSlot">The generated writer's active slot.</param>
    /// <param name="hasRows">Whether the active slot contains rows to write.</param>
    protected void Complete(TSlot activeSlot, bool hasRows)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(activeSlot);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfNotInitialized();
            if (!_completed)
            {
                if (hasRows && _fault is null)
                    EnqueueReadySlotNoLock(activeSlot);

                _completed = true;
                _addingCompleted = true;
            }
        }

        SignalWorkers();

        for (var i = 0; i < _workers.Length; i++)
            _workers[i].Join();

        ThrowIfFaulted();
        _writer.CloseFile();
    }

    /// <summary>Aborts the pipeline and releases its workers, pooled buffers, and destination.</summary>
    /// <remarks>
    /// Disposing does not complete the Parquet file. Call the generated writer's <c>Complete()</c> method before
    /// disposal to commit a valid file. Once disposed, the writer cannot be reused.
    /// </remarks>
    public void Dispose()
    {
        for (var i = 0; i < _workers.Length; i++)
            if (ReferenceEquals(Thread.CurrentThread, _workers[i]))
                throw new InvalidOperationException("A row writer cannot be disposed from one of its worker threads.");

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _addingCompleted = true;
            for (var i = 0; i < _workerReadySlots.Length; i++)
                _workerReadySlots[i].Clear();
        }

        SignalWorkers();
        _freeSignal.Release();

        for (var i = 0; i < _workers.Length; i++)
            if (_workers[i] is { IsAlive: true } worker)
                worker.Join();

        ExceptionDispatchInfo? cleanupFailure = null;
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is not { } slot)
                continue;
            try
            {
                ResetSlotForReuse(slot);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        try
        {
            _writer.Dispose();
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(ex);
        }

        for (var i = 0; i < _workerReadySignals.Length; i++)
            _workerReadySignals[i].Dispose();
        _freeSignal.Dispose();
        cleanupFailure?.Throw();
    }

    /// <summary>Rethrows a failure reported by a serialization worker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void ThrowIfFaulted()
    {
        ThrowIfDisposed();
        RethrowFault();
    }

    /// <summary>Throws if this writer has been disposed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().FullName);
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

    void StartWorkersNoLock()
    {
        for (var i = 0; i < _workers.Length; i++)
        {
            var workerIndex = i;
            _workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"{WorkerThreadNamePrefix}-{i}"
            };
            _workers[i].Start(workerIndex);
        }
    }

    void EnqueueReadySlotNoLock(TSlot slot)
    {
        if (!_slotOwners.TryGetValue(slot, out var workerIndex))
            throw new InvalidOperationException("The row-writer slot does not belong to this pipeline.");
        _workerReadySlots[workerIndex].Enqueue(new QueuedSlot(slot, _nextQueuedSequence++));
        _workerReadySignals[workerIndex].Release();
    }

    TSlot TakeFreeSlot()
    {
        while (true)
        {
            _freeSignal.Wait();
            lock (_gate)
            {
                ThrowIfDisposed();
                RethrowFault();
                if (_freeSlots.Count != 0)
                    return _freeSlots.Dequeue();
            }
        }
    }

    void WorkerLoop(object? state)
    {
        var workerIndex = (int)state!;
        var readySlots = _workerReadySlots[workerIndex];
        var readySignal = _workerReadySignals[workerIndex];
        var workerName = Thread.CurrentThread.Name ?? $"{WorkerThreadNamePrefix}-{workerIndex}";
        try
        {
            _execution.OnWorkerStarted?.Invoke(new ParquetWorkerContext(workerIndex, _workers.Length, workerName));
        }
        catch (Exception ex)
        {
            RecordFault(ex);
        }

        while (true)
        {
            readySignal.Wait();
            QueuedSlot queuedSlot;
            lock (_gate)
            {
                if (_disposed || _fault is not null || readySlots.Count == 0 && _addingCompleted)
                    return;
                if (readySlots.Count == 0)
                    continue;

                queuedSlot = readySlots.Dequeue();
            }

            try
            {
                SerializeSlot(queuedSlot.Slot);
                EnqueueSerializedSlot(queuedSlot);
            }
            catch (Exception ex)
            {
                RecordFault(ex);
                ReturnSlot(queuedSlot.Slot);
            }
        }
    }

    void EnqueueSerializedSlot(QueuedSlot queuedSlot)
    {
        // A serializer deposits its result and immediately stops competing for the ordered-write lock.
        // Whichever serializer makes the next sequence available becomes the sole ordered drainer.
        var drain = false;
        lock (_writeGate)
        {
            if (_disposed)
            {
                ReturnSlot(queuedSlot.Slot);
                return;
            }
            RethrowFault();
            _serializedSlots.Add(queuedSlot.Sequence, queuedSlot);
            if (!_writerActive && _serializedSlots.ContainsKey(_nextWriteSequence))
            {
                _writerActive = true;
                drain = true;
            }
        }

        if (drain)
            DrainSerializedSlots();
    }

    void DrainSerializedSlots()
    {
        while (true)
        {
            QueuedSlot queuedSlot;
            lock (_writeGate)
            {
                if (_disposed || _fault is not null ||
                    !_serializedSlots.Remove(_nextWriteSequence, out queuedSlot))
                {
                    _writerActive = false;
                    return;
                }
            }

            try
            {
                if (_rolloverPending)
                    Rollover();
                var rowGroupWriter = _writer.StartRowGroup();
                WriteSerializedSlot(queuedSlot.Slot, rowGroupWriter);
                OnSlotWritten(queuedSlot.Slot);
                if (_rollingFile is not null && checked((ulong)_writer.FileOffset) >= _targetFileSizeBytes)
                    _rolloverPending = true;
                _nextWriteSequence++;
            }
            catch (Exception ex)
            {
                RecordFault(ex);
            }
            finally
            {
                ReturnSlot(queuedSlot.Slot);
            }
        }
    }

    void ReturnSlot(TSlot slot)
    {
        try
        {
            ResetSlotForReuse(slot);
        }
        catch (Exception ex)
        {
            RecordFault(ex);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;
            _freeSlots.Enqueue(slot);
        }
        _freeSignal.Release();
    }

    void RecordFault(Exception exception)
    {
        var captured = ExceptionDispatchInfo.Capture(exception);
        if (Interlocked.CompareExchange(ref _fault, captured, null) is not null)
            return;

        lock (_gate)
        {
            _addingCompleted = true;
        }

        SignalWorkers();
        _freeSignal.Release();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void RethrowFault()
    {
        var fault = _fault;
        if (fault is not null)
            fault.Throw();
    }

    static Queue<QueuedSlot>[] CreateWorkerReadySlots(int workerCount)
    {
        var queues = new Queue<QueuedSlot>[workerCount];
        for (var i = 0; i < queues.Length; i++)
            queues[i] = new Queue<QueuedSlot>(1);
        return queues;
    }

    static SemaphoreSlim[] CreateWorkerReadySignals(int workerCount)
    {
        var signals = new SemaphoreSlim[workerCount];
        for (var i = 0; i < signals.Length; i++)
            signals[i] = new SemaphoreSlim(0);
        return signals;
    }

    void SignalWorkers()
    {
        for (var i = 0; i < _workerReadySignals.Length; i++)
            _workerReadySignals[i].Release();
    }

    void Rollover()
    {
        var file = _rollingFile ?? throw new InvalidOperationException("The row writer does not have a rolling file.");
        var filePath = _filePath ?? throw new InvalidOperationException("The row writer does not have a file path selector.");
        _writer.FinishFile();
        _fileIndex = checked(_fileIndex + 1);
        OpenRollingFile(file, filePath, _fileIndex, _bufferPool);
        _writer.Reset(file);
        _rolloverPending = false;
    }

    static void OpenRollingFile(IParquetWriteSource file, ParquetFilePath filePath, ulong fileIndex,
        IParquetBufferPool bufferPool)
    {
        var path = filePath(fileIndex, bufferPool, out var allocation);
        try
        {
            if (path.IsEmpty)
                throw new InvalidOperationException("The row writer file path selector returned an empty path.");
            file.Open(path, FileMode.Create);
        }
        finally
        {
            if (allocation is { } owner)
                owner.Dispose();
        }
    }

    readonly struct QueuedSlot(TSlot slot, ulong sequence)
    {
        internal TSlot Slot { get; } = slot;
        internal ulong Sequence { get; } = sequence;
    }
}
