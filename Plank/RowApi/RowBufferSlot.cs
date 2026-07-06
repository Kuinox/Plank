using Plank.Writing;

namespace Plank.RowApi;

public abstract class RowBufferSlot
{
    readonly int _rowCount;
    readonly RowApiColumnWriteState[] _columns;
    List<IDisposable>? _ownedBuffers;

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

    protected int Index { get; private set; }

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
