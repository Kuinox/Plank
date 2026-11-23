namespace Plank.RowApi;

/// <summary>The repetition levels, definition levels and dense values of one row of a repeated leaf.</summary>
/// <typeparam name="TValue">The leaf's physical value type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// The row has one level pair per position and one value per position at the leaf's maximum definition level.
/// An empty row means the leaf has no data for it, such as a column missing from the file.
/// </remarks>
public readonly ref struct RowApiNestedLeafRow<TValue>
{
    internal RowApiNestedLeafRow(ReadOnlySpan<byte> repetitionLevels, ReadOnlySpan<byte> definitionLevels,
        ReadOnlySpan<TValue> values)
    {
        RepetitionLevels = repetitionLevels;
        DefinitionLevels = definitionLevels;
        Values = values;
    }

    /// <summary>Gets the repetition level of each position.</summary>
    public ReadOnlySpan<byte> RepetitionLevels { get; }

    /// <summary>Gets the definition level of each position.</summary>
    public ReadOnlySpan<byte> DefinitionLevels { get; }

    /// <summary>Gets the values of the positions defined up to the leaf, in order.</summary>
    public ReadOnlySpan<TValue> Values { get; }
}
