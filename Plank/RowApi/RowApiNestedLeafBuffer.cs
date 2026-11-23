using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.RowApi;

/// <summary>Buffers one repeated leaf of a generated row writer as Parquet levels and dense values.</summary>
/// <typeparam name="TValue">The leaf's physical value type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// Generated code writes each row between <see cref="BeginRow"/> and <see cref="EndRow"/>, adding one position per
/// list element, or per null or empty collection. Levels are appended in row order; a row that is assigned again
/// leaves its previous levels unused until the buffer is serialized. A row keeps its previous value until
/// <see cref="EndRow"/>, so an assignment that throws part way leaves the row unchanged.
/// </remarks>
public sealed class RowApiNestedLeafBuffer<TValue>
{
    readonly LeafColumn _column;
    readonly int _maxRepetitionLevel;
    readonly int _maxDefinitionLevel;
    // The definition level of an unassigned row, which is written as a null collection, or -1 if it cannot be null.
    readonly int _unassignedDefinitionLevel;
    byte[] _repetitionLevels = [];
    byte[] _definitionLevels = [];
    TValue[] _values = [];
    RowRange[] _rows;
    int _levelCount;
    int _valueCount;
    int _pendingRow = -1;
    int _pendingLevelStart;
    int _pendingValueStart;

    internal RowApiNestedLeafBuffer(LeafColumn column, int definedDefinitionLevel, int rowCount)
    {
        _column = column;
        _maxRepetitionLevel = column.MaxRepetitionLevel;
        _maxDefinitionLevel = column.MaxDefinitionLevel;
        _unassignedDefinitionLevel = definedDefinitionLevel - 1;
        _rows = rowCount == 0 ? [] : new RowRange[rowCount];
    }

    // Dataset buffers copy binary values so callers can reuse their arrays after queueing a row.
    internal bool SnapshotBinary { get; set; }

    /// <summary>Starts writing the row at <paramref name="rowIndex"/>; <see cref="EndRow"/> replaces any value it had.</summary>
    /// <param name="rowIndex">The row index in the buffer slot.</param>
    public void BeginRow(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)_rows.Length)
            throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, "Row index is outside the buffer slot.");
        _pendingRow = rowIndex;
        _pendingLevelStart = _levelCount;
        _pendingValueStart = _valueCount;
    }

    /// <summary>Adds a position that is not defined up to the leaf.</summary>
    /// <param name="repetitionLevel">The position's repetition level.</param>
    /// <param name="definitionLevel">The position's definition level, below the leaf's maximum.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddNull(int repetitionLevel, int definitionLevel)
    {
        if ((uint)definitionLevel >= (uint)_maxDefinitionLevel)
            ThrowDefinitionLevel(definitionLevel);
        AddLevels(repetitionLevel, definitionLevel);
    }

    /// <summary>Adds a position that holds a value.</summary>
    /// <param name="repetitionLevel">The position's repetition level.</param>
    /// <param name="value">The physical value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int repetitionLevel, TValue value)
    {
        if (!typeof(TValue).IsValueType && value is null)
            throw new InvalidOperationException($"Column '{_column.Path}' cannot store a null value; add a null position instead.");
        if (typeof(TValue) == typeof(byte[]) && SnapshotBinary)
            value = (TValue)(object)Unsafe.As<TValue, byte[]>(ref value).AsSpan().ToArray();
        AddLevels(repetitionLevel, _maxDefinitionLevel);
        if (_valueCount == _values.Length)
            Array.Resize(ref _values, Math.Max(16, checked(_values.Length * 2)));
        _values[_valueCount++] = value;
    }

    /// <summary>Completes the row started by <see cref="BeginRow"/>.</summary>
    public void EndRow()
    {
        if (_pendingRow < 0)
            throw new InvalidOperationException("No nested row is being written.");
        var levelCount = _levelCount - _pendingLevelStart;
        if (levelCount == 0 || _repetitionLevels[_pendingLevelStart] != 0)
            throw new InvalidOperationException($"A row of column '{_column.Path}' must start with a position at repetition level 0.");
        _rows[_pendingRow] = new RowRange(_pendingLevelStart, levelCount, _pendingValueStart, _valueCount - _pendingValueStart);
        _pendingRow = -1;
    }

    /// <summary>Gets the levels and values written for a row; empty if it was not written.</summary>
    /// <param name="rowIndex">The row index in the buffer slot.</param>
    /// <returns>The row's levels and values.</returns>
    public RowApiNestedLeafRow<TValue> GetRow(int rowIndex)
    {
        var range = _rows[rowIndex];
        return new(_repetitionLevels.AsSpan(range.LevelStart, range.LevelCount),
            _definitionLevels.AsSpan(range.LevelStart, range.LevelCount),
            _values.AsSpan(range.ValueStart, range.ValueCount));
    }

    /// <summary>Estimates the buffered size of a row.</summary>
    /// <param name="rowIndex">The row index in the buffer slot.</param>
    /// <returns>The estimated number of encoded bytes.</returns>
    public ulong EstimateRowSize(int rowIndex)
    {
        var range = _rows[rowIndex];
        var size = (ulong)Math.Max(range.LevelCount, 1);
        if (typeof(TValue) != typeof(byte[]))
            return checked(size + (ulong)range.ValueCount * (ulong)Unsafe.SizeOf<TValue>());
        var values = _values.AsSpan(range.ValueStart, range.ValueCount);
        for (var i = 0; i < values.Length; i++)
            size = checked(size + (ulong)Math.Max(Unsafe.As<TValue, byte[]>(ref values[i]).Length, 1));
        return size;
    }

    internal void Resize(int rowCount)
        => Array.Resize(ref _rows, rowCount);

    internal void Reset()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            Array.Clear(_values, 0, _valueCount);
        Array.Clear(_rows);
        _levelCount = 0;
        _valueCount = 0;
        _pendingRow = -1;
    }

    internal void ClearRows(int start, int count)
        => Array.Clear(_rows, start, count);

    internal void CopyRowTo(int sourceIndex, RowApiNestedLeafBuffer<TValue> destination, int destinationIndex)
    {
        var range = _rows[sourceIndex];
        if (range.LevelCount == 0)
        {
            destination._rows[destinationIndex] = default;
            return;
        }

        destination.BeginRow(destinationIndex);

        destination.EnsureLevelCapacity(range.LevelCount);
        _repetitionLevels.AsSpan(range.LevelStart, range.LevelCount)
            .CopyTo(destination._repetitionLevels.AsSpan(destination._levelCount));
        _definitionLevels.AsSpan(range.LevelStart, range.LevelCount)
            .CopyTo(destination._definitionLevels.AsSpan(destination._levelCount));
        destination._levelCount += range.LevelCount;
        destination.EnsureValueCapacity(range.ValueCount);
        _values.AsSpan(range.ValueStart, range.ValueCount).CopyTo(destination._values.AsSpan(destination._valueCount));
        destination._valueCount += range.ValueCount;
        destination.EndRow();
    }

    /// <summary>Returns the levels and values of the first <paramref name="rowCount"/> rows in row order.</summary>
    internal void GetRows(int rowCount, out ReadOnlySpan<byte> repetitionLevels, out ReadOnlySpan<byte> definitionLevels,
        out ReadOnlySpan<TValue> values)
    {
        var levelCount = 0;
        var valueCount = 0;
        for (var i = 0; i < rowCount; i++)
        {
            var range = _rows[i];
            if (range.LevelCount == 0 || range.LevelStart != levelCount || range.ValueStart != valueCount)
            {
                Compact(rowCount);
                (levelCount, valueCount) = (0, 0);
                for (var row = 0; row < rowCount; row++)
                    (levelCount, valueCount) = (levelCount + _rows[row].LevelCount, valueCount + _rows[row].ValueCount);
                break;
            }
            levelCount += range.LevelCount;
            valueCount += range.ValueCount;
        }

        repetitionLevels = _repetitionLevels.AsSpan(0, levelCount);
        definitionLevels = _definitionLevels.AsSpan(0, levelCount);
        values = _values.AsSpan(0, valueCount);
    }

    // Rewrites the buffer in row order, dropping replaced rows and writing unassigned rows as null collections.
    void Compact(int rowCount)
    {
        var levelCount = 0;
        var valueCount = 0;
        for (var i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].LevelCount == 0 && i >= rowCount)
                continue;
            levelCount = checked(levelCount + Math.Max(_rows[i].LevelCount, 1));
            valueCount = checked(valueCount + _rows[i].ValueCount);
        }

        var repetitionLevels = new byte[Math.Max(levelCount, 16)];
        var definitionLevels = new byte[repetitionLevels.Length];
        var values = new TValue[Math.Max(valueCount, 16)];
        levelCount = 0;
        valueCount = 0;
        for (var i = 0; i < _rows.Length; i++)
        {
            var range = _rows[i];
            if (range.LevelCount == 0)
            {
                if (i >= rowCount)
                    continue;
                if (_unassignedDefinitionLevel < 0)
                    throw new InvalidOperationException(
                        $"Row {i} of column '{_column.Path}' was not assigned, and its collection cannot be null.");
                repetitionLevels[levelCount] = 0;
                definitionLevels[levelCount] = (byte)_unassignedDefinitionLevel;
                _rows[i] = new RowRange(levelCount++, 1, valueCount, 0);
                continue;
            }

            _repetitionLevels.AsSpan(range.LevelStart, range.LevelCount).CopyTo(repetitionLevels.AsSpan(levelCount));
            _definitionLevels.AsSpan(range.LevelStart, range.LevelCount).CopyTo(definitionLevels.AsSpan(levelCount));
            _values.AsSpan(range.ValueStart, range.ValueCount).CopyTo(values.AsSpan(valueCount));
            _rows[i] = new RowRange(levelCount, range.LevelCount, valueCount, range.ValueCount);
            levelCount += range.LevelCount;
            valueCount += range.ValueCount;
        }

        _repetitionLevels = repetitionLevels;
        _definitionLevels = definitionLevels;
        _values = values;
        _levelCount = levelCount;
        _valueCount = valueCount;
        _pendingRow = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void AddLevels(int repetitionLevel, int definitionLevel)
    {
        if ((uint)repetitionLevel > (uint)_maxRepetitionLevel)
            ThrowRepetitionLevel(repetitionLevel);
        if (_levelCount == _repetitionLevels.Length)
            EnsureLevelCapacity(1);
        _repetitionLevels[_levelCount] = (byte)repetitionLevel;
        _definitionLevels[_levelCount] = (byte)definitionLevel;
        _levelCount++;
    }

    void EnsureLevelCapacity(int additional)
    {
        var required = checked(_levelCount + additional);
        if (required <= _repetitionLevels.Length)
            return;
        var capacity = Math.Max(Math.Max(16, required), checked(_repetitionLevels.Length * 2));
        Array.Resize(ref _repetitionLevels, capacity);
        Array.Resize(ref _definitionLevels, capacity);
    }

    void EnsureValueCapacity(int additional)
    {
        var required = checked(_valueCount + additional);
        if (required > _values.Length)
            Array.Resize(ref _values, Math.Max(Math.Max(16, required), checked(_values.Length * 2)));
    }

    void ThrowRepetitionLevel(int repetitionLevel)
        => throw new ArgumentOutOfRangeException(nameof(repetitionLevel), repetitionLevel,
            $"Column '{_column.Path}' has maximum repetition level {_maxRepetitionLevel}.");

    void ThrowDefinitionLevel(int definitionLevel)
        => throw new ArgumentOutOfRangeException(nameof(definitionLevel), definitionLevel,
            $"A null position of column '{_column.Path}' needs a definition level below {_maxDefinitionLevel}.");

    readonly record struct RowRange(int LevelStart, int LevelCount, int ValueStart, int ValueCount);
}
