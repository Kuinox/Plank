using System.Runtime.ExceptionServices;
using Plank.IO.ZeroAlloc;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Dataset;

/// <summary>Provides the bounded partition state used by generated dataset writers.</summary>
/// <typeparam name="TRow">The generated schema row type.</typeparam>
/// <typeparam name="TSlot">The generated row-buffer slot type.</typeparam>
/// <remarks>This unstable API supports Plank-generated code and is not intended for direct use.</remarks>
public abstract class DatasetWriterBase<TRow, TSlot>
    where TSlot : RowBufferSlot
{
    readonly ParquetSchema _schema;
    readonly DatasetWriterOptions _options;
    readonly IParquetBufferPool _bufferPool;
    readonly PartitionState[] _states;
    readonly int _maximumActiveWriters;
    readonly int _maximumPendingPartitions;
    readonly int _activationRowCount;
    int _activeWriterCount;
    int _pendingPartitionCount;
    ulong _clock;
    bool _initialized;
    bool _disposed;

    /// <summary>Initializes the state used by a generated dataset writer.</summary>
    /// <param name="schema">The one schema shared by all files in the dataset.</param>
    /// <param name="rowBufferCapacity">The row capacity of each generated buffer slot.</param>
    /// <param name="options">The dataset writer options.</param>
    protected DatasetWriterBase(ParquetSchema schema, int rowBufferCapacity, DatasetWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        if (rowBufferCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowBufferCapacity), rowBufferCapacity,
                "Row buffer capacity must be greater than zero.");

        options.Validate(rowBufferCapacity);
        _schema = schema;
        _options = options;
        _bufferPool = options.AppendOptions.WriterOptions.BufferPool;
        _maximumActiveWriters = checked((int)options.MaximumActiveWriters);
        _maximumPendingPartitions = checked((int)options.MaximumPendingPartitions);
        _activationRowCount = checked((int)options.RowsBeforeWriterActivation);
        _states = new PartitionState[checked(_maximumActiveWriters + _maximumPendingPartitions)];
        _activeWriterCount = 0;
        _pendingPartitionCount = 0;
        _clock = 0;
        _initialized = false;
        _disposed = false;
    }

    /// <summary>Creates an unbound generated row-buffer slot.</summary>
    /// <returns>A new row-buffer slot.</returns>
    protected abstract TSlot CreateSlot();

    /// <summary>Copies one schema row into a generated row-buffer slot.</summary>
    /// <param name="row">The source schema row.</param>
    /// <param name="slot">The destination row-buffer slot.</param>
    protected abstract void CopyRow(TRow row, TSlot slot);

    /// <summary>Gets the UTF-8 path for one schema row.</summary>
    /// <param name="row">The schema row.</param>
    /// <param name="bufferPool">The writer buffer pool.</param>
    /// <param name="allocation">
    /// The optional allocation that owns the returned path. Set it to null when the path has another owner.
    /// </param>
    /// <returns>The full UTF-8 file path.</returns>
    protected abstract ReadOnlySpan<byte> SelectPath(TRow row, IParquetBufferPool bufferPool,
        out ParquetBuffer? allocation);

    /// <summary>Creates the fixed set of generated row buffers.</summary>
    protected void InitializeSlots()
    {
        if (_initialized)
            throw new InvalidOperationException("Dataset writer slots are already initialized.");

        for (var i = 0; i < _states.Length; i++)
        {
            var slot = CreateSlot();
            ArgumentNullException.ThrowIfNull(slot);
            _states[i] = new PartitionState(slot);
        }

        _initialized = true;
    }

    /// <summary>Routes and queues one row.</summary>
    /// <param name="row">The row to queue.</param>
    protected void QueueRow(TRow row)
    {
        ThrowIfUnavailable();
        if (row is null)
            throw new ArgumentNullException(nameof(row));

        ParquetBuffer? selectedPathAllocation = null;
        try
        {
            var path = SelectPath(row, _bufferPool, out selectedPathAllocation);
            if (path.IsEmpty)
                throw new InvalidOperationException("The dataset path selector returned an empty path.");

            var state = FindState(path);
            if (state is null)
                state = AddState(path, ref selectedPathAllocation);

            state.LastUse = unchecked(++_clock);
            CopyRow(row, state.Slot);
            state.Slot.Next();

            if (state.Kind == PartitionKind.Pending &&
                (state.Slot.Count >= _activationRowCount || _pendingPartitionCount > _maximumPendingPartitions))
                Activate(state);

            if (state.Kind == PartitionKind.Active && state.Slot.IsFull)
                WriteRows(state);
        }
        finally
        {
            if (selectedPathAllocation is { } allocation)
                allocation.Dispose();
        }
    }

    /// <summary>Writes all pending rows and closes all active files.</summary>
    protected void DisposeDataset()
    {
        if (_disposed)
            throw new InvalidOperationException("The dataset writer is already disposed.");
        _disposed = true;

        ExceptionDispatchInfo? failure = null;
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            if (state.Kind == PartitionKind.Free)
                continue;

            try
            {
                if (state.Kind == PartitionKind.Pending)
                    Activate(state);
                CloseAndRelease(state);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
                ReleaseAfterFailure(state);
            }
        }

        failure?.Throw();
    }

    PartitionState? FindState(ReadOnlySpan<byte> path)
    {
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            if (state.Kind != PartitionKind.Free && state.PathLength == path.Length &&
                path.SequenceEqual(state.Path.Span[..state.PathLength]))
                return state;
        }

        return null;
    }

    PartitionState AddState(ReadOnlySpan<byte> path, ref ParquetBuffer? selectedPathAllocation)
    {
        var state = FindFreeState();
        if (state is null)
        {
            state = FindLeastRecentlyUsedActive();
            if (state is null)
                throw new InvalidOperationException("The dataset writer has no reusable partition state.");
            CloseAndRelease(state);
        }

        var pathBuffer = TakePathBuffer(path, ref selectedPathAllocation);

        state.Path = pathBuffer;
        state.PathLength = path.Length;
        state.Kind = PartitionKind.Pending;
        state.LastUse = unchecked(++_clock);
        _pendingPartitionCount++;

        return state;
    }

    ParquetBuffer TakePathBuffer(ReadOnlySpan<byte> path, ref ParquetBuffer? selectedPathAllocation)
    {
        if (selectedPathAllocation is not { } owner)
        {
            var copy = _bufferPool.Rent(checked((uint)path.Length));
            try
            {
                path.CopyTo(copy.Span);
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }

        var ownerSpan = (ReadOnlySpan<byte>)owner.Span;
        if (!ownerSpan.Overlaps(path, out var offset) || offset < 0 || path.Length > ownerSpan.Length - offset)
            throw new InvalidOperationException("The path allocation does not own the returned path span.");

        selectedPathAllocation = null;
        if (offset == 0)
            return owner;

        var slice = owner.RetainSlice(offset, path.Length);
        owner.Dispose();
        return slice;
    }

    PartitionState? FindFreeState()
    {
        for (var i = 0; i < _states.Length; i++)
            if (_states[i].Kind == PartitionKind.Free)
                return _states[i];
        return null;
    }

    PartitionState? FindLeastRecentlyUsedActive()
    {
        PartitionState? result = null;
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            if (state.Kind == PartitionKind.Active && (result is null || state.LastUse < result.LastUse))
                result = state;
        }

        return result;
    }

    void Activate(PartitionState state)
    {
        if (state.Kind != PartitionKind.Pending)
            return;

        if (_activeWriterCount == _maximumActiveWriters)
        {
            var victim = FindLeastRecentlyUsedActive();
            if (victim is null)
                throw new InvalidOperationException("The dataset writer has no active writer to evict.");
            CloseAndRelease(victim);
        }

        var stream = state.Stream;
        stream.Open(state.Path.Span[..state.PathLength], FileMode.OpenOrCreate, FileAccess.ReadWrite);
        ParquetWriter? writer = null;
        try
        {
            writer = stream.Length == 0
                ? _schema.CreateWriter(stream, _options.AppendOptions.WriterOptions)
                : _schema.CreateAppender(stream, _options.AppendOptions);
            state.Slot.Bind(writer);
        }
        catch
        {
            state.Slot.Unbind();
            if (writer is null)
                stream.CloseFile();
            else
                try
                {
                    writer.CloseFile();
                }
                catch
                {
                }
                finally
                {
                    stream.CloseFile();
                }
            throw;
        }

        state.Writer = writer;
        state.Kind = PartitionKind.Active;
        _pendingPartitionCount--;
        _activeWriterCount++;
    }

    void WriteRows(PartitionState state)
    {
        if (state.Kind != PartitionKind.Active || state.Writer is null)
            throw new InvalidOperationException("The partition does not have an active writer.");
        if (state.Slot.IsEmpty)
            return;

        state.Slot.SerializeColumns();
        var rowGroupWriter = state.Writer.StartRowGroup();
        state.Slot.WriteSerialized(rowGroupWriter);
        state.Slot.ResetForReuse();
    }

    void CloseAndRelease(PartitionState state)
    {
        if (state.Kind != PartitionKind.Active || state.Writer is null)
            throw new InvalidOperationException("The partition does not have an active writer.");

        WriteRows(state);
        state.Writer.CloseFile();
        state.Slot.Unbind();
        state.Writer = null;
        _activeWriterCount--;
        ReleaseState(state);
    }

    void ReleaseAfterFailure(PartitionState state)
    {
        if (state.Kind == PartitionKind.Active)
        {
            state.Stream.CloseFile();
            state.Slot.Unbind();
            state.Writer = null;
            _activeWriterCount--;
        }
        else if (state.Kind == PartitionKind.Pending)
            _pendingPartitionCount--;

        state.Slot.ResetForReuse();
        ReleaseState(state);
    }

    static void ReleaseState(PartitionState state)
    {
        state.Path.Dispose();
        state.Path = default;
        state.PathLength = 0;
        state.LastUse = 0;
        state.Kind = PartitionKind.Free;
    }

    void ThrowIfUnavailable()
    {
        if (!_initialized)
            throw new InvalidOperationException("Dataset writer slots are not initialized.");
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    sealed class PartitionState(TSlot slot)
    {
        internal readonly TSlot Slot = slot;
        internal readonly ReusableFileWriteStream Stream = new();
        internal ParquetBuffer Path;
        internal int PathLength;
        internal ParquetWriter? Writer;
        internal ulong LastUse;
        internal PartitionKind Kind;
    }

    enum PartitionKind : byte
    {
        Free,
        Pending,
        Active
    }
}
