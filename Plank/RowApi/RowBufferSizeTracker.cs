namespace Plank.RowApi;

sealed class RowBufferSizeTracker
{
    readonly RowApiColumnWriteState[] _variableSizeColumns;
    readonly ulong _fixedRowSizeBytes;

    internal RowBufferSizeTracker(RowApiColumnWriteState[] columns)
    {
        ulong fixedRowSizeBytes = 0;
        var variableCount = 0;
        for (var i = 0; i < columns.Length; i++)
            if (columns[i].FixedValueSizeBytes is { } size)
                fixedRowSizeBytes = checked(fixedRowSizeBytes + size);
            else
                variableCount++;

        _fixedRowSizeBytes = fixedRowSizeBytes;
        _variableSizeColumns = variableCount == 0 ? [] : new RowApiColumnWriteState[variableCount];
        var variableIndex = 0;
        for (var i = 0; i < columns.Length; i++)
            if (columns[i].FixedValueSizeBytes is null)
                _variableSizeColumns[variableIndex++] = columns[i];
    }

    internal ulong GetRowSize(int index)
    {
        var size = _fixedRowSizeBytes;
        for (var i = 0; i < _variableSizeColumns.Length; i++)
            size = checked(size + _variableSizeColumns[i].GetValueSize(index));
        return size;
    }
}
