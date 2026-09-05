using System.Buffers;
using System.Runtime.CompilerServices;
using Plank.RowApi;
using Plank.Schema;

namespace Plank.Dataset;

/// <summary>Owns the binary values copied by Dataset Queue until they are written or discarded.</summary>
sealed class DatasetBufferSlot : RowBufferSlot
{
    const uint SnapshotChunkSize = 64 * 1024;
    readonly RowBufferSizeTracker _sizeTracker;
    readonly IParquetBufferPool _bufferPool;
    readonly RowApiColumnDescriptor[] _columns;
    readonly SnapshotMemory[]?[] _snapshots;
    ParquetBuffer _currentChunk;
    int _chunkOffset;

    internal DatasetBufferSlot(RowApiColumnDescriptor[] columns, int rowCount, IParquetBufferPool bufferPool)
        : base(columns, rowCount)
    {
        _columns = columns;
        _bufferPool = bufferPool;
        _sizeTracker = CreateSizeTracker();
        _snapshots = new SnapshotMemory[columns.Length][];
        for (var i = 0; i < columns.Length; i++)
            if (columns[i] is RowApiColumnDescriptor<ReadOnlyMemory<byte>> or
                RowApiColumnDescriptor<ReadOnlyMemory<byte>?>)
                _snapshots[i] = CreateSnapshots(rowCount);
    }

    internal ulong BufferedSizeBytes { get; private set; }

    // Dataset buffers may use memory-backed storage for arrays: the wire representation is identical,
    // and the caller's array does not need a separate managed allocation for every queued value.
    internal static RowApiColumnDescriptor[] CreateSnapshotColumns(RowApiColumnDescriptor[] columns)
    {
        var result = (RowApiColumnDescriptor[])columns.Clone();
        for (var i = 0; i < result.Length; i++)
        {
            var column = result[i];
            if (column is RowApiColumnDescriptor<byte[]>)
                result[i] = column.Column.MaxDefinitionLevel > 0
                    ? new RowApiColumnDescriptor<ReadOnlyMemory<byte>?>(column.PropertyName, column.Column)
                    : new RowApiColumnDescriptor<ReadOnlyMemory<byte>>(column.PropertyName, column.Column);
        }
        return result;
    }

    internal void SetSnapshotValue<T>(int columnIndex, int rowIndex, T value)
    {
        if (typeof(T) == typeof(byte[]))
        {
            var bytes = Unsafe.As<T, byte[]?>(ref value);
            if (_columns[columnIndex].Column.MaxDefinitionLevel > 0)
                SetValue<ReadOnlyMemory<byte>?>(columnIndex, rowIndex,
                    bytes is null ? (ReadOnlyMemory<byte>?)null : Snapshot(columnIndex, rowIndex, bytes));
            else
            {
                ArgumentNullException.ThrowIfNull(bytes);
                SetValue(columnIndex, rowIndex, Snapshot(columnIndex, rowIndex, bytes));
            }
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            SetValue(columnIndex, rowIndex,
                Snapshot(columnIndex, rowIndex, Unsafe.As<T, ReadOnlyMemory<byte>>(ref value).Span));
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            var memory = Unsafe.As<T, ReadOnlyMemory<byte>?>(ref value);
            SetValue<ReadOnlyMemory<byte>?>(columnIndex, rowIndex,
                memory is { } bytes ? Snapshot(columnIndex, rowIndex, bytes.Span) : (ReadOnlyMemory<byte>?)null);
        }
        else if (value is Array shape && _columns[columnIndex].Column.PhysicalType is
                 ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray)
            SetValue(columnIndex, rowIndex, (T)(object)SnapshotBinaryShape(shape));
        else
            SetValue(columnIndex, rowIndex, value);
    }

    // Nested generated projections already allocate jagged arrays, but their binary leaves may
    // still refer to caller-owned arrays. Preserve the shape and snapshot every binary endpoint.
    static Array SnapshotBinaryShape(Array shape)
    {
        if (shape is byte[] bytes)
            return bytes.ToArray();
        var copy = (Array)shape.Clone();
        for (var i = 0; i < copy.Length; i++)
            if (copy.GetValue(i) is Array child)
                copy.SetValue(SnapshotBinaryShape(child), i);
        return copy;
    }

    ReadOnlyMemory<byte> Snapshot(int columnIndex, int rowIndex, ReadOnlySpan<byte> bytes)
    {
        var snapshot = _snapshots[columnIndex]![rowIndex];
        snapshot.Reset();
        if (bytes.IsEmpty)
            return ReadOnlyMemory<byte>.Empty;

        if (bytes.Length > _currentChunk.Length - _chunkOffset)
        {
            _currentChunk.Dispose();
            _chunkOffset = 0;
            _currentChunk = _bufferPool.Rent(Math.Max(SnapshotChunkSize, checked((uint)bytes.Length)));
        }

        bytes.CopyTo(_currentChunk.Span.Slice(_chunkOffset, bytes.Length));
        var memory = snapshot.SetBuffer(_currentChunk.RetainSlice(_chunkOffset, bytes.Length));
        _chunkOffset += bytes.Length;
        return memory;
    }

    internal void MoveRowTo(int sourceIndex, DatasetBufferSlot destination, int destinationIndex)
    {
        CopyRowTo(sourceIndex, destination, destinationIndex);
        for (var i = 0; i < _snapshots.Length; i++)
        {
            if (_snapshots[i] is not { } snapshots)
                continue;
            // The copied memory refers to this adapter. Move it with its retained slice and
            // give the parked position the destination's spare adapter for its next value.
            var target = destination._snapshots[i]!;
            (snapshots[sourceIndex], target[destinationIndex]) = (target[destinationIndex], snapshots[sourceIndex]);
        }
    }

    internal new void ClearRow(int index)
    {
        base.ClearRow(index);
        for (var i = 0; i < _snapshots.Length; i++)
            _snapshots[i]?[index].Reset();
    }

    protected override void OnBuffersResized()
    {
        for (var i = 0; i < _snapshots.Length; i++)
        {
            if (_snapshots[i] is not { } snapshots)
                continue;
            var rowCount = _columns[i] is RowApiColumnDescriptor<ReadOnlyMemory<byte>>
                ? GetValues<ReadOnlyMemory<byte>>(i).Length
                : GetValues<ReadOnlyMemory<byte>?>(i).Length;
            var previousCount = snapshots.Length;
            Array.Resize(ref snapshots, rowCount);
            for (var rowIndex = previousCount; rowIndex < rowCount; rowIndex++)
                snapshots[rowIndex] = new SnapshotMemory();
            _snapshots[i] = snapshots;
        }
    }

    static SnapshotMemory[] CreateSnapshots(int rowCount)
    {
        var snapshots = new SnapshotMemory[rowCount];
        for (var i = 0; i < rowCount; i++)
            snapshots[i] = new SnapshotMemory();
        return snapshots;
    }

    internal void NextSized()
    {
        BufferedSizeBytes = checked(BufferedSizeBytes + _sizeTracker.GetRowSize(Count));
        Next();
    }

    internal void ResetForReuseAndSize()
    {
        ResetForReuse();
        for (var i = 0; i < _snapshots.Length; i++)
        {
            if (_snapshots[i] is not { } snapshots)
                continue;
            for (var rowIndex = 0; rowIndex < snapshots.Length; rowIndex++)
                snapshots[rowIndex].Reset();
        }
        _currentChunk.Dispose();
        _chunkOffset = 0;
        BufferedSizeBytes = 0;
    }

    // Preallocated with column capacity and reused, including across parked-row promotion.
    // Only adapts a retained ParquetBuffer slice to the serializer's ReadOnlyMemory API;
    // ParquetBuffer supplies all reference counting and allocation ownership.
    sealed class SnapshotMemory : MemoryManager<byte>
    {
        ParquetBuffer _buffer;

        internal ReadOnlyMemory<byte> SetBuffer(ParquetBuffer buffer)
        {
            _buffer = buffer;
            return Memory;
        }

        internal void Reset() => _buffer.Dispose();

        public override Span<byte> GetSpan() => _buffer.Span;

        // These private snapshots are consumed synchronously as spans by the serializer.
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() { }

        protected override void Dispose(bool disposing) => Reset();
    }
}
