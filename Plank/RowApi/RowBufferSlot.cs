using System.Runtime.CompilerServices;
using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

/// <summary>Provides column buffers for a generated row writer batch.</summary>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public abstract class RowBufferSlot
{
    int _rowCount;
    readonly RowApiColumnWriteState[] _columns;
    List<IDisposable>? _ownedBuffers;

    internal RowBufferSlot(RowApiColumnDescriptor[] columns, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be non-negative.");

        _rowCount = rowCount;
        _columns = CreateColumnStates(columns, rowCount);
        Index = 0;
    }

    /// <summary>Initializes a generated buffer slot that writes directly to a row group.</summary>
    /// <param name="rowGroupWriter">The destination row-group writer.</param>
    /// <param name="columns">The generated column descriptors.</param>
    /// <param name="rowCount">The slot's row capacity.</param>
    protected RowBufferSlot(RowGroupWriter rowGroupWriter, RowApiColumnDescriptor[] columns, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(rowGroupWriter);
        ArgumentNullException.ThrowIfNull(columns);
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be non-negative.");

        _rowCount = rowCount;
        _columns = CreateColumnStates(rowGroupWriter, columns, rowCount);
        Index = 0;
    }

    /// <summary>Initializes a generated buffer slot whose columns can be serialized in parallel.</summary>
    /// <param name="writer">The destination Parquet writer.</param>
    /// <param name="columns">The generated column descriptors.</param>
    /// <param name="rowCount">The slot's row capacity.</param>
    protected RowBufferSlot(ParquetWriter writer, RowApiColumnDescriptor[] columns, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be non-negative.");

        _rowCount = rowCount;
        _columns = CreateColumnStates(writer, columns, rowCount);
        Index = 0;
    }

    internal bool IsFull
        => Index == _rowCount;

    internal bool IsEmpty
        => Index == 0;

    internal int Count
        => Index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAdvanceBefore(int rowsPerGroup)
    {
        var next = unchecked(Index + 1);
        // The unsigned capacity comparison also rejects integer wraparound.
        // Leave the boundary row to the checked cold path.
        if ((uint)next >= (uint)_rowCount || next >= rowsPerGroup)
            return false;
        Index = next;
        return true;
    }

    internal void Bind(ParquetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        for (var i = 0; i < _columns.Length; i++)
            _columns[i].Bind(writer);
    }

    internal void Unbind()
    {
        for (var i = 0; i < _columns.Length; i++)
            _columns[i].Unbind();
    }

    /// <summary>Gets the index at which the generated writer stores the next row.</summary>
    protected int Index { get; private set; }

    /// <summary>Gets a typed column array used by the generated writer.</summary>
    /// <typeparam name="T">The column's generated CLR value type.</typeparam>
    /// <param name="columnIndex">The column index in the generated row schema.</param>
    /// <returns>The column's writable value array.</returns>
    protected T[] GetValues<T>(int columnIndex)
    {
        if ((uint)columnIndex >= (uint)_columns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnIndex), columnIndex,
                "Column index is outside the row API schema.");

        if (_columns[columnIndex] is RowApiColumnWriteState<T> state)
            return state.Values;

        throw new InvalidOperationException($"Row API column at index {columnIndex} cannot be written as {typeof(T)}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Next()
    {
        if (Index >= _rowCount)
            throw new InvalidOperationException("No more row slots are available.");
        Index++;
    }

    internal ulong GetRowSize()
    {
        if (Index >= _rowCount)
            throw new InvalidOperationException("No more row slots are available.");

        ulong size = 0;
        for (var i = 0; i < _columns.Length; i++)
            size = checked(size + (_columns[i].FixedValueSizeBytes ?? _columns[i].GetValueSize(Index)));
        return size;
    }

    internal RowBufferSizeTracker CreateSizeTracker()
        => new(_columns);

    internal bool Grow()
    {
        if (_rowCount == int.MaxValue)
            return false;

        var rowCount = _rowCount == 0 ? 1 : checked((int)Math.Min((long)_rowCount * 2, int.MaxValue));
        for (var i = 0; i < _columns.Length; i++)
            _columns[i].Resize(rowCount);
        _rowCount = rowCount;
        OnBuffersResized();
        return true;
    }

    /// <summary>Refreshes generated typed buffer references after the slot grows.</summary>
    protected virtual void OnBuffersResized()
    {
    }

    /// <summary>Registers a resource to dispose when this slot is reused.</summary>
    /// <param name="owner">The resource that owns values stored in the slot.</param>
    public void RegisterOwner(IDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        (_ownedBuffers ??= []).Add(owner);
    }

    internal void SerializeColumns()
    {
        for (var i = 0; i < _columns.Length; i++)
            _columns[i].Serialize(Index);
    }

    internal void WriteSerialized(RowGroupWriter rowGroupWriter)
    {
        ArgumentNullException.ThrowIfNull(rowGroupWriter);

        for (var i = 0; i < _columns.Length; i++)
            _columns[i].Write(rowGroupWriter);
    }

    internal void ResetForReuse()
    {
        if (_ownedBuffers is not null)
        {
            for (var i = 0; i < _ownedBuffers.Count; i++)
                _ownedBuffers[i].Dispose();
            _ownedBuffers.Clear();
        }

        for (var i = 0; i < _columns.Length; i++)
            _columns[i].ResetForReuse(0, Index);
        Index = 0;
    }

    internal void ClearRow(int index)
    {
        ValidateRowIndex(index);
        for (var i = 0; i < _columns.Length; i++)
            _columns[i].ResetForReuse(index, 1);
    }

    internal void SetValue<T>(int columnIndex, int rowIndex, T value)
    {
        ValidateRowIndex(rowIndex);
        if ((uint)columnIndex >= (uint)_columns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnIndex), columnIndex,
                "Column index is outside the row API schema.");
        if (_columns[columnIndex] is not RowApiColumnWriteState<T> state)
            throw new InvalidOperationException($"Row API column at index {columnIndex} cannot be written as {typeof(T)}.");
        state.Values[rowIndex] = value;
    }

    internal void CopyRowTo(int sourceIndex, RowBufferSlot destination, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateRowIndex(sourceIndex);
        destination.ValidateRowIndex(destinationIndex);
        if (_columns.Length != destination._columns.Length)
            throw new InvalidOperationException("Row API buffer schemas do not match.");
        for (var i = 0; i < _columns.Length; i++)
            _columns[i].CopyValueTo(sourceIndex, destination._columns[i], destinationIndex);
    }

    /// <summary>Throws if the generated writer has filled this slot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void EnsureRowAvailable()
    {
        if (Index >= _rowCount)
            throw new InvalidOperationException("No more row slots are available.");
    }

    /// <summary>Estimates the buffered size of one generated variable-width value.</summary>
    /// <typeparam name="T">The generated CLR storage type.</typeparam>
    /// <param name="value">The value stored for the current row.</param>
    /// <param name="physicalType">The generated Parquet physical type.</param>
    /// <param name="typeLength">The generated fixed-length binary size, or zero otherwise.</param>
    /// <returns>The estimated number of buffered bytes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static ulong EstimateValueSize<T>(T value, ParquetPhysicalType physicalType, uint typeLength)
        => RowValueSizeEstimator.Estimate(value, physicalType, typeLength);

    static RowApiColumnWriteState[] CreateColumnStates(RowGroupWriter rowGroupWriter, RowApiColumnDescriptor[] columns,
        int rowCount)
    {
        var states = new RowApiColumnWriteState[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i] ?? throw new ArgumentException("Row API column descriptors cannot contain null values.",
                nameof(columns));
            states[i] = column.CreateWriteState(rowGroupWriter, rowCount);
        }

        return states;
    }

    void ValidateRowIndex(int index)
    {
        if ((uint)index >= (uint)_rowCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Row index is outside the buffer slot.");
    }

    static RowApiColumnWriteState[] CreateColumnStates(RowApiColumnDescriptor[] columns, int rowCount)
    {
        var states = new RowApiColumnWriteState[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i] ?? throw new ArgumentException("Row API column descriptors cannot contain null values.",
                nameof(columns));
            states[i] = column.CreateWriteState(rowCount);
        }

        return states;
    }

    static RowApiColumnWriteState[] CreateColumnStates(ParquetWriter writer, RowApiColumnDescriptor[] columns,
        int rowCount)
    {
        var states = new RowApiColumnWriteState[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i] ?? throw new ArgumentException("Row API column descriptors cannot contain null values.",
                nameof(columns));
            states[i] = column.CreateWriteState(writer, rowCount);
        }

        return states;
    }

}
