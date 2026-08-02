namespace Plank.RowApi;

/// <summary>Describes one repeated collection level used by a generated nested row column.</summary>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public readonly struct RowApiCollectionLevel
{
    /// <summary>Initializes one generated collection-level descriptor.</summary>
    /// <param name="repetitionLevel">The repetition level that starts another element.</param>
    /// <param name="definedDefinitionLevel">The definition level at which the collection is non-null.</param>
    /// <param name="elementDefinitionLevel">The definition level at which the collection has an element.</param>
    public RowApiCollectionLevel(int repetitionLevel, int definedDefinitionLevel, int elementDefinitionLevel)
    {
        RepetitionLevel = repetitionLevel;
        DefinedDefinitionLevel = definedDefinitionLevel;
        ElementDefinitionLevel = elementDefinitionLevel;
    }

    /// <summary>Gets the repetition level that starts another element in this collection.</summary>
    public int RepetitionLevel { get; }

    /// <summary>Gets the definition level at which the collection is non-null.</summary>
    public int DefinedDefinitionLevel { get; }

    /// <summary>Gets the definition level at which the collection has an element.</summary>
    public int ElementDefinitionLevel { get; }
}
