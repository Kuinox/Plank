using Plank.Writing;

namespace Plank.RowApi;

/// <summary>Provides column buffers for a generated row writer batch.</summary>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public abstract class RowBufferSlot
{
    readonly int _rowCount;
    readonly RowApiColumnWriteState[] _columns;
    List<IDisposable>? _ownedBuffers;

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

    internal void Next()
    {
        if (Index >= _rowCount)
            throw new InvalidOperationException("No more row slots are available.");
        Index++;
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
            _columns[i].ResetForReuse(Index);
        Index = 0;
    }

    /// <summary>Throws if the generated writer has filled this slot.</summary>
    protected void EnsureRowAvailable()
    {
        if (Index >= _rowCount)
            throw new InvalidOperationException("No more row slots are available.");
    }

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
