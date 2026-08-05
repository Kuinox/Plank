using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

/// <summary>Describes an allocating nested row projection over one physical leaf.</summary>
/// <typeparam name="TShape">The jagged row shape serialized for the leaf.</typeparam>
/// <typeparam name="TElement">The dense materialized leaf value type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public sealed class RowApiNestedColumnDescriptor<TShape, TElement> : RowApiColumnDescriptor
{
    internal readonly RowApiCollectionLevel[] CollectionLevels;

    /// <summary>Initializes a descriptor for a generated nested row property leaf.</summary>
    /// <param name="propertyName">The generated row property's name.</param>
    /// <param name="column">The corresponding physical leaf.</param>
    /// <param name="collectionLevels">The collection thresholds from outermost to innermost.</param>
    public RowApiNestedColumnDescriptor(string propertyName, LeafColumn column,
        params RowApiCollectionLevel[] collectionLevels)
        : base(propertyName, column)
    {
        ArgumentNullException.ThrowIfNull(collectionLevels);
        if (column.MaxRepetitionLevel == 0)
            throw new ArgumentException($"Leaf '{column.Path}' is not repeated.", nameof(column));
        if (collectionLevels.Length != column.MaxRepetitionLevel)
            throw new ArgumentException(
                $"Nested row shape declares {collectionLevels.Length} collection levels, but leaf '{column.Path}' has maximum repetition level {column.MaxRepetitionLevel}.",
                nameof(collectionLevels));

        for (var i = 0; i < collectionLevels.Length; i++)
        {
            var level = collectionLevels[i];
            if (level.RepetitionLevel != i + 1)
                throw new ArgumentException("Collection repetition levels must be consecutive and one-based.",
                    nameof(collectionLevels));
            if (level.DefinedDefinitionLevel < 0 ||
                level.ElementDefinitionLevel <= level.DefinedDefinitionLevel ||
                level.ElementDefinitionLevel > column.MaxDefinitionLevel)
                throw new ArgumentException("Collection definition thresholds are outside the leaf definition range.",
                    nameof(collectionLevels));
        }

        var shapeType = typeof(TShape);
        for (var i = 0; i < collectionLevels.Length; i++)
        {
            if (!shapeType.IsArray || shapeType.GetArrayRank() != 1)
                throw new ArgumentException("Nested row shapes must use one-dimensional arrays at every collection level.",
                    nameof(collectionLevels));
            shapeType = shapeType.GetElementType()!;
        }
        if ((Nullable.GetUnderlyingType(shapeType) ?? shapeType) != typeof(TElement))
            throw new ArgumentException(
                $"Nested row shape leaf type '{shapeType}' does not match dense element type '{typeof(TElement)}'.",
                nameof(collectionLevels));

        CollectionLevels = (RowApiCollectionLevel[])collectionLevels.Clone();
    }

    internal override RowApiColumnReadState CreateState()
        => new RowApiNestedColumnReadState<TShape, TElement>(this);

    internal override RowApiColumnWriteState CreateWriteState(RowGroupWriter rowGroupWriter, int rowCount)
        => new RowApiColumnWriteState<TShape>(this, rowGroupWriter, rowCount);

    internal override RowApiColumnWriteState CreateWriteState(int rowCount)
        => new RowApiColumnWriteState<TShape>(this, rowCount);

    internal override RowApiColumnWriteState CreateWriteState(ParquetWriter writer, int rowCount)
        => new RowApiColumnWriteState<TShape>(this, writer, rowCount);
}
