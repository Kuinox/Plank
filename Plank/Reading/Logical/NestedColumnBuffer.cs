namespace Plank.Reading.Logical;

/// <summary>
/// Exposes one decoded page as dense materialized values and its repetition and definition level streams.
/// </summary>
/// <remarks>
/// The spans remain valid until the enumerator advances or is disposed. Use <see cref="ColumnBuffer{T}.Retain"/>
/// and <see cref="RetainLevels"/> when the values or levels must outlive that operation.
/// </remarks>
public readonly struct NestedColumnBuffer<T>
{
    internal readonly ParquetBuffer NativeLevels;
    readonly int _levelCount;
    readonly int _rowCount;
    readonly bool _startsWithContinuation;
    readonly int _maxRepetitionLevel;
    readonly int _maxDefinitionLevel;

    internal NestedColumnBuffer(ColumnBuffer<T> values, ParquetBuffer levels, int levelCount, int rowCount,
        bool startsWithContinuation, int maxRepetitionLevel, int maxDefinitionLevel)
    {
        Values = values;
        NativeLevels = levels;
        _levelCount = levelCount;
        _rowCount = rowCount;
        _startsWithContinuation = startsWithContinuation;
        _maxRepetitionLevel = maxRepetitionLevel;
        _maxDefinitionLevel = maxDefinitionLevel;
    }

    /// <summary>Gets the dense physical values whose definition level equals <see cref="MaxDefinitionLevel"/>.</summary>
    public ColumnBuffer<T> Values { get; }

    /// <summary>Gets the repetition level for every logical entry in the page.</summary>
    public ReadOnlySpan<int> RepetitionLevels
        => _levelCount == 0 ? [] : ParquetBuffer.AsReadOnlySpan<int>(NativeLevels, _levelCount);

    /// <summary>Gets the definition level for every logical entry in the page.</summary>
    public ReadOnlySpan<int> DefinitionLevels
        => _levelCount == 0
            ? []
            : ParquetBuffer.AsReadOnlySpan<int>(NativeLevels, checked(_levelCount * 2))[_levelCount..];

    /// <summary>Gets the number of logical entries and level pairs in the page.</summary>
    public int Count
        => _levelCount;

    /// <summary>Gets the number of row starts in the page.</summary>
    public int RowCount
        => _rowCount;

    /// <summary>Gets whether the first entry continues a row that began on an earlier page.</summary>
    public bool StartsWithContinuation
        => _startsWithContinuation;

    /// <summary>Gets the maximum repetition level for the leaf.</summary>
    public int MaxRepetitionLevel
        => _maxRepetitionLevel;

    /// <summary>Gets the maximum definition level for the leaf.</summary>
    public int MaxDefinitionLevel
        => _maxDefinitionLevel;

    /// <summary>Retains the pooled repetition and definition level storage.</summary>
    public ParquetBuffer RetainLevels()
    {
        if (_levelCount == 0)
            return default;
        return NativeLevels.RetainSlice(0, checked(_levelCount * 2 * sizeof(int)));
    }
}
