using System.Runtime.ExceptionServices;
using Plank.Reading;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Dataset;

/// <summary>Provides the bounded partition state used by generated dataset writers.</summary>
/// <typeparam name="TRow">The generated schema row type.</typeparam>
/// <remarks>This unstable API supports Plank-generated code and is not intended for direct use.</remarks>
public abstract class DatasetWriterBase<TRow>
{
    readonly ParquetSchema _schema;
    readonly RowApiColumnDescriptor[] _columns;
    readonly DatasetWriterOptions _options;
    readonly IParquetBufferPool _bufferPool;
    readonly PartitionState[] _states;
    readonly FileSources[] _files;
    readonly PendingKeyState[] _pendingKeys;
    readonly int[] _parkedRowKeys;
    readonly int[] _parkedRowLinks;
    readonly int _rowBufferCapacity;
    readonly int _pendingRowCapacity;
    DatasetBufferSlot _parkedRows = null!;
    int _availableFileCount;
    int _parkedRowCount;
    int _parkedRowHead;
    int _parkedRowTail;
    int _freeParkedRowHead;
    ulong _clock;
    bool _initialized;
    bool _disposed;

    /// <summary>Initializes the state used by a generated dataset writer.</summary>
    /// <param name="schema">The one schema shared by all files in the dataset.</param>
    /// <param name="columns">The generated column descriptors for the schema.</param>
    /// <param name="rowBufferCapacity">The row capacity of each generated buffer slot.</param>
    /// <param name="files">The fixed reusable file sources.</param>
    /// <param name="options">The dataset writer options.</param>
    protected DatasetWriterBase(ParquetSchema schema, RowApiColumnDescriptor[] columns, int rowBufferCapacity,
        IParquetWriteSource[] files, DatasetWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        if (rowBufferCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowBufferCapacity), rowBufferCapacity,
                "Row buffer capacity must be greater than zero.");
        if (files.Length == 0)
            throw new ArgumentException("At least one file source is required.", nameof(files));

        options.Validate();
        _schema = schema;
        _columns = columns;
        _options = options;
        _bufferPool = options.AppendOptions.WriterOptions.BufferPool;
        _rowBufferCapacity = rowBufferCapacity;
        _pendingRowCapacity = checked((int)options.PendingRowCapacity);
        _states = new PartitionState[files.Length];
        _files = new FileSources[files.Length];
        _pendingKeys = new PendingKeyState[_pendingRowCapacity];
        _parkedRowKeys = new int[_pendingRowCapacity];
        _parkedRowLinks = new int[_pendingRowCapacity];
        _availableFileCount = 0;
        _parkedRowHead = -1;
        _parkedRowTail = -1;
        _freeParkedRowHead = _pendingRowCapacity == 0 ? -1 : 0;

        for (var i = 0; i < _files.Length; i++)
        {
            var destination = files[i] ?? throw new ArgumentException("A file source cannot be null.", nameof(files));
            if (destination is not IParquetReadSource source)
                throw new ArgumentException("Each file source must also implement IParquetReadSource.", nameof(files));
            for (var previous = 0; previous < i; previous++)
                if (ReferenceEquals(destination, _files[previous].Destination))
                    throw new ArgumentException("A file source cannot occur more than once.", nameof(files));
            _files[i] = new FileSources(source, destination);
            _availableFileCount++;
        }

        for (var i = 0; i < _parkedRowLinks.Length; i++)
            _parkedRowLinks[i] = i + 1 < _parkedRowLinks.Length ? i + 1 : -1;
    }

    /// <summary>Copies one schema row into the dataset column buffers.</summary>
    /// <param name="row">The source schema row.</param>
    /// <param name="slotIndex">The destination slot identifier.</param>
    /// <param name="rowIndex">The destination row index.</param>
    protected abstract void CopyRow(TRow row, int slotIndex, int rowIndex);

    /// <summary>Sets one typed value in a dataset column buffer.</summary>
    /// <typeparam name="T">The generated column value type.</typeparam>
    /// <param name="slotIndex">The destination slot identifier.</param>
    /// <param name="columnIndex">The destination column index.</param>
    /// <param name="rowIndex">The destination row index.</param>
    /// <param name="value">The value to set.</param>
    protected void SetColumnValue<T>(int slotIndex, int columnIndex, int rowIndex, T value)
        => GetSlot(slotIndex).SetValue(columnIndex, rowIndex, value);

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
            var slot = new DatasetBufferSlot(_columns, _rowBufferCapacity);
            _states[i] = new PartitionState(slot, i);
        }

        _parkedRows = new DatasetBufferSlot(_columns, _pendingRowCapacity);
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

            var active = FindActiveState(path);
            if (active is not null)
            {
                QueueActiveRow(row, active);
                return;
            }

            var pendingKeyIndex = FindPendingKey(path);
            if (pendingKeyIndex < 0 && _availableFileCount > 0)
            {
                active = ActivatePath(path, ref selectedPathAllocation);
                QueueActiveRow(row, active);
                return;
            }

            if (_pendingRowCapacity == 0)
            {
                active = ActivatePath(path, ref selectedPathAllocation);
                QueueActiveRow(row, active);
                return;
            }

            if (_parkedRowCount == _pendingRowCapacity)
            {
                PromoteLatestParkedPartition();
                active = FindActiveState(path);
                if (active is not null)
                {
                    QueueActiveRow(row, active);
                    return;
                }
                pendingKeyIndex = FindPendingKey(path);
            }

            var addedPendingKey = pendingKeyIndex < 0;
            if (addedPendingKey)
                pendingKeyIndex = AddPendingKey(path, ref selectedPathAllocation);
            try
            {
                ParkRow(row, pendingKeyIndex);
            }
            catch
            {
                if (addedPendingKey)
                    ReleaseEmptyPendingKey(pendingKeyIndex);
                throw;
            }

            if (_parkedRowCount == _pendingRowCapacity)
                PromotePendingPartition(pendingKeyIndex);
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
            if (!state.Active)
                continue;

            try
            {
                CloseAndRelease(state);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
                TryReleaseAfterFailure(state, ref failure);
            }
        }

        while (_parkedRowCount > 0)
        {
            var keyIndex = _parkedRowKeys[_parkedRowTail];
            try
            {
                var state = PromotePendingPartition(keyIndex);
                CloseAndRelease(state);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
                DiscardPendingPartition(keyIndex, ref failure);
            }
        }

        _parkedRows.ResetForReuse();
        failure?.Throw();
    }

    void QueueActiveRow(TRow row, PartitionState state)
    {
        state.LastUse = unchecked(++_clock);
        CopyRow(row, state.SlotIndex, state.Slot.Count);
        state.Slot.Next();
        if (state.Slot.IsFull)
            WriteRows(state);
    }

    PartitionState? FindActiveState(ReadOnlySpan<byte> path)
    {
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            if (state.Active && state.PathLength == path.Length &&
                path.SequenceEqual(state.Path.Span[..state.PathLength]))
                return state;
        }

        return null;
    }

    int FindPendingKey(ReadOnlySpan<byte> path)
    {
        for (var i = 0; i < _pendingKeys.Length; i++)
        {
            ref var key = ref _pendingKeys[i];
            if (key.Used && key.PathLength == path.Length && path.SequenceEqual(key.Path.Span[..key.PathLength]))
                return i;
        }

        return -1;
    }

    int AddPendingKey(ReadOnlySpan<byte> path, ref ParquetBuffer? selectedPathAllocation)
    {
        for (var i = 0; i < _pendingKeys.Length; i++)
        {
            ref var key = ref _pendingKeys[i];
            if (key.Used)
                continue;

            key.Path = TakePathBuffer(path, ref selectedPathAllocation);
            key.PathLength = path.Length;
            key.RowCount = 0;
            key.Used = true;
            return i;
        }

        throw new InvalidOperationException("The parked-row pool has no free partition key.");
    }

    void ParkRow(TRow row, int keyIndex)
    {
        if (_freeParkedRowHead < 0)
            throw new InvalidOperationException("The parked-row pool is full.");

        var rowIndex = _freeParkedRowHead;
        var nextFree = _parkedRowLinks[rowIndex];
        try
        {
            CopyRow(row, _states.Length, rowIndex);
        }
        catch
        {
            _parkedRows.ClearRow(rowIndex);
            throw;
        }
        _freeParkedRowHead = nextFree;
        _parkedRowKeys[rowIndex] = keyIndex;
        _parkedRowLinks[rowIndex] = -1;
        if (_parkedRowTail < 0)
            _parkedRowHead = rowIndex;
        else
            _parkedRowLinks[_parkedRowTail] = rowIndex;
        _parkedRowTail = rowIndex;
        _parkedRowCount++;
        _pendingKeys[keyIndex].RowCount++;
    }

    void ReleaseEmptyPendingKey(int keyIndex)
    {
        ref var key = ref _pendingKeys[keyIndex];
        if (!key.Used || key.RowCount != 0)
            return;
        key.Path.Dispose();
        key = default;
    }

    void PromoteLatestParkedPartition()
    {
        if (_parkedRowTail < 0)
            throw new InvalidOperationException("The parked-row pool is empty.");
        PromotePendingPartition(_parkedRowKeys[_parkedRowTail]);
    }

    PartitionState PromotePendingPartition(int keyIndex)
    {
        ref var key = ref _pendingKeys[keyIndex];
        if (!key.Used || key.RowCount == 0)
            throw new InvalidOperationException("The pending partition has no parked rows.");

        var state = ActivatePathBuffer(key.Path, key.PathLength);
        key.Path = default;
        try
        {
            DrainPendingRows(keyIndex, state);
            key = default;
            return state;
        }
        catch (Exception exception)
        {
            var failure = ExceptionDispatchInfo.Capture(exception);
            try
            {
                ReleaseAfterFailure(state);
            }
            catch
            {
            }
            ExceptionDispatchInfo? cleanupFailure = null;
            DiscardPendingPartition(keyIndex, ref cleanupFailure);
            failure.Throw();
            throw;
        }
    }

    void DrainPendingRows(int keyIndex, PartitionState state)
    {
        var previous = -1;
        var current = _parkedRowHead;
        while (current >= 0)
        {
            var next = _parkedRowLinks[current];
            if (_parkedRowKeys[current] != keyIndex)
            {
                previous = current;
                current = next;
                continue;
            }

            _parkedRows.CopyRowTo(current, state.Slot, state.Slot.Count);
            state.Slot.Next();
            RemoveParkedRow(current, previous, next);
            _pendingKeys[keyIndex].RowCount--;
            if (state.Slot.IsFull)
                WriteRows(state);
            current = next;
        }
    }

    void RemoveParkedRow(int rowIndex, int previous, int next)
    {
        if (previous < 0)
            _parkedRowHead = next;
        else
            _parkedRowLinks[previous] = next;
        if (_parkedRowTail == rowIndex)
            _parkedRowTail = previous;

        _parkedRows.ClearRow(rowIndex);
        _parkedRowKeys[rowIndex] = -1;
        _parkedRowLinks[rowIndex] = _freeParkedRowHead;
        _freeParkedRowHead = rowIndex;
        _parkedRowCount--;
    }

    PartitionState ActivatePath(ReadOnlySpan<byte> path, ref ParquetBuffer? selectedPathAllocation)
    {
        var pathBuffer = TakePathBuffer(path, ref selectedPathAllocation);
        try
        {
            return ActivatePathBuffer(pathBuffer, path.Length);
        }
        catch
        {
            pathBuffer.Dispose();
            throw;
        }
    }

    PartitionState ActivatePathBuffer(ParquetBuffer pathBuffer, int pathLength)
    {
        if (_availableFileCount == 0)
        {
            var victim = FindLeastBusyActive();
            if (victim is null)
                throw new InvalidOperationException("The dataset writer has no active writer to return a file.");
            CloseAndRelease(victim);
        }

        var state = FindFreeState() ??
            throw new InvalidOperationException("The dataset writer has no free active partition state.");
        var file = TakeFile();
        ParquetWriter? writer = null;
        try
        {
            file.Destination.Open(pathBuffer.Span[..pathLength], FileMode.OpenOrCreate);
            writer = file.Source.Length == 0
                ? _schema.CreateWriter(file.Destination, _options.AppendOptions.WriterOptions)
                : _schema.CreateAppender(file.Source, file.Destination, _options.AppendOptions);
            state.Slot.Bind(writer);
        }
        catch
        {
            state.Slot.Unbind();
            CloseFailedOpen(file, writer);
            ReturnFile(file);
            throw;
        }

        state.Path = pathBuffer;
        state.PathLength = pathLength;
        state.File = file;
        state.Writer = writer;
        state.LastUse = unchecked(++_clock);
        state.Active = true;
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
            if (!_states[i].Active)
                return _states[i];
        return null;
    }

    PartitionState? FindLeastBusyActive()
    {
        PartitionState? result = null;
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            if (!state.Active)
                continue;
            if (result is null || state.Slot.Count < result.Slot.Count ||
                state.Slot.Count == result.Slot.Count && state.LastUse < result.LastUse)
                result = state;
        }

        return result;
    }

    void WriteRows(PartitionState state)
    {
        if (!state.Active || state.Writer is null)
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
        if (!state.Active || state.Writer is null || state.File is not { } file)
            throw new InvalidOperationException("The partition does not have an active writer.");

        WriteRows(state);
        state.Writer.CloseFile();
        state.Slot.Unbind();
        state.File = null;
        state.Writer = null;
        state.Active = false;
        ReturnFile(file);
        ReleaseState(state);
    }

    void TryReleaseAfterFailure(PartitionState state, ref ExceptionDispatchInfo? failure)
    {
        try
        {
            ReleaseAfterFailure(state);
        }
        catch (Exception cleanupException)
        {
            failure ??= ExceptionDispatchInfo.Capture(cleanupException);
        }
    }

    void ReleaseAfterFailure(PartitionState state)
    {
        if (!state.Active)
            return;

        var file = state.File;
        try
        {
            if (file is { } sources)
                sources.Destination.Close();
        }
        finally
        {
            state.Slot.Unbind();
            state.Slot.ResetForReuse();
            state.File = null;
            state.Writer = null;
            state.Active = false;
            if (file is { } sources)
                ReturnFile(sources);
            ReleaseState(state);
        }
    }

    void DiscardPendingPartition(int keyIndex, ref ExceptionDispatchInfo? failure)
    {
        ref var key = ref _pendingKeys[keyIndex];
        try
        {
            var previous = -1;
            var current = _parkedRowHead;
            while (current >= 0)
            {
                var next = _parkedRowLinks[current];
                if (_parkedRowKeys[current] == keyIndex)
                    RemoveParkedRow(current, previous, next);
                else
                    previous = current;
                current = next;
            }
        }
        catch (Exception cleanupException)
        {
            failure ??= ExceptionDispatchInfo.Capture(cleanupException);
        }
        finally
        {
            key.Path.Dispose();
            key = default;
        }
    }

    static void CloseFailedOpen(FileSources file, ParquetWriter? writer)
    {
        if (writer is null)
        {
            try
            {
                file.Destination.Close();
            }
            catch
            {
            }
            return;
        }

        try
        {
            writer.CloseFile();
        }
        catch
        {
            try
            {
                file.Destination.Close();
            }
            catch
            {
            }
        }
    }

    static void ReleaseState(PartitionState state)
    {
        state.Path.Dispose();
        state.Path = default;
        state.PathLength = 0;
        state.LastUse = 0;
    }

    FileSources TakeFile()
    {
        if (_availableFileCount == 0)
            throw new InvalidOperationException("The dataset writer has no reusable file available.");

        var index = --_availableFileCount;
        var file = _files[index];
        _files[index] = default;
        return file;
    }

    void ReturnFile(FileSources file)
    {
        if (_availableFileCount >= _files.Length)
            throw new InvalidOperationException("The dataset writer file pool is already full.");
        _files[_availableFileCount++] = file;
    }

    DatasetBufferSlot GetSlot(int slotIndex)
    {
        if ((uint)slotIndex < (uint)_states.Length)
            return _states[slotIndex].Slot;
        if (slotIndex == _states.Length)
            return _parkedRows;
        throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex,
            "Slot index is outside the dataset writer.");
    }

    void ThrowIfUnavailable()
    {
        if (!_initialized)
            throw new InvalidOperationException("Dataset writer slots are not initialized.");
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    sealed class PartitionState(DatasetBufferSlot slot, int slotIndex)
    {
        internal readonly DatasetBufferSlot Slot = slot;
        internal readonly int SlotIndex = slotIndex;
        internal ParquetBuffer Path;
        internal int PathLength;
        internal FileSources? File;
        internal ParquetWriter? Writer;
        internal ulong LastUse;
        internal bool Active;
    }

    sealed class DatasetBufferSlot(RowApiColumnDescriptor[] columns, int rowCount)
        : RowBufferSlot(columns, rowCount);

    struct PendingKeyState
    {
        internal ParquetBuffer Path;
        internal int PathLength;
        internal int RowCount;
        internal bool Used;
    }

    readonly struct FileSources(IParquetReadSource source, IParquetWriteSource destination)
    {
        internal readonly IParquetReadSource Source = source;
        internal readonly IParquetWriteSource Destination = destination;
    }
}
