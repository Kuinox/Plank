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
    readonly SnapshotChunk?[]?[] _owners;
    SnapshotChunk? _currentChunk;
    int _chunkOffset;

    internal DatasetBufferSlot(RowApiColumnDescriptor[] columns, int rowCount, IParquetBufferPool bufferPool)
        : base(columns, rowCount)
    {
        _columns = columns;
        _bufferPool = bufferPool;
        _sizeTracker = CreateSizeTracker();
        _owners = new SnapshotChunk?[columns.Length][];
        for (var i = 0; i < columns.Length; i++)
            if (columns[i] is RowApiColumnDescriptor<ReadOnlyMemory<byte>> or
                RowApiColumnDescriptor<ReadOnlyMemory<byte>?>)
                _owners[i] = new SnapshotChunk?[rowCount];
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
        var owners = _owners[columnIndex]!;
        owners[rowIndex]?.Release();
        owners[rowIndex] = null;
        if (bytes.IsEmpty)
            return ReadOnlyMemory<byte>.Empty;

        if (_currentChunk is null || bytes.Length > _currentChunk.GetSpan().Length - _chunkOffset)
        {
            _currentChunk?.Release();
            _currentChunk = null;
            _chunkOffset = 0;
            _currentChunk = new SnapshotChunk(_bufferPool.Rent(Math.Max(SnapshotChunkSize, checked((uint)bytes.Length))));
        }

        var memory = _currentChunk.Memory.Slice(_chunkOffset, bytes.Length);
        bytes.CopyTo(memory.Span);
        _chunkOffset += bytes.Length;
        _currentChunk.Retain();
        owners[rowIndex] = _currentChunk;
        return memory;
    }

    internal void MoveRowTo(int sourceIndex, DatasetBufferSlot destination, int destinationIndex)
    {
        CopyRowTo(sourceIndex, destination, destinationIndex);
        for (var i = 0; i < _owners.Length; i++)
        {
            if (_owners[i] is not { } owners)
                continue;
            destination._owners[i]![destinationIndex] = owners[sourceIndex];
            owners[sourceIndex] = null;
        }
    }

    internal new void ClearRow(int index)
    {
        base.ClearRow(index);
        for (var i = 0; i < _owners.Length; i++)
        {
            if (_owners[i] is not { } owners)
                continue;
            owners[index]?.Release();
            owners[index] = null;
        }
    }

    protected override void OnBuffersResized()
    {
        for (var i = 0; i < _owners.Length; i++)
        {
            if (_owners[i] is null)
                continue;
            var rowCount = _columns[i] is RowApiColumnDescriptor<ReadOnlyMemory<byte>>
                ? GetValues<ReadOnlyMemory<byte>>(i).Length
                : GetValues<ReadOnlyMemory<byte>?>(i).Length;
            Array.Resize(ref _owners[i], rowCount);
        }
    }

    internal void NextSized()
    {
        BufferedSizeBytes = checked(BufferedSizeBytes + _sizeTracker.GetRowSize(Count));
        Next();
    }

    internal void ResetForReuseAndSize()
    {
        ResetForReuse();
        for (var i = 0; i < _owners.Length; i++)
        {
            if (_owners[i] is not { } owners)
                continue;
            for (var rowIndex = 0; rowIndex < owners.Length; rowIndex++)
            {
                owners[rowIndex]?.Release();
                owners[rowIndex] = null;
            }
        }
        _currentChunk?.Release();
        _currentChunk = null;
        _chunkOffset = 0;
        BufferedSizeBytes = 0;
    }

    // Each buffered value and the appending slot hold one reference. Promotion moves the value's
    // reference to the active slot, so clearing/reusing a parked row cannot invalidate its bytes.
    sealed class SnapshotChunk(ParquetBuffer buffer) : MemoryManager<byte>
    {
        ParquetBuffer _buffer = buffer;
        int _references = 1;

        internal void Retain()
            => _references++;

        internal void Release()
        {
            if (--_references == 0)
                ((IDisposable)this).Dispose();
        }

        public override Span<byte> GetSpan()
            => _buffer.Span;

        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            Retain();
            return new MemoryHandle((byte*)_buffer.DangerousGetAddress() + elementIndex, pinnable: this);
        }

        public override void Unpin()
            => Release();

        protected override void Dispose(bool disposing)
            => _buffer.Dispose();
    }
}
