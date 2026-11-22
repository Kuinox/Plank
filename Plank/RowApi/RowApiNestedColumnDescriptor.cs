using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

/// <summary>Describes one repeated leaf of a generated nested row property.</summary>
/// <typeparam name="TValue">The leaf's physical value type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// Generated code writes and reads the leaf as repetition levels, definition levels and dense values.
/// </remarks>
public sealed class RowApiNestedColumnDescriptor<TValue> : RowApiColumnDescriptor
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
        if (column.MaxDefinitionLevel > byte.MaxValue)
            throw new ArgumentException($"Leaf '{column.Path}' is nested too deeply for the generated row API.",
                nameof(column));
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

        if (!IsPhysicalType(column.Column.PhysicalType))
            throw new ArgumentException(
                $"Leaf '{column.Path}' with physical type {column.Column.PhysicalType} cannot be buffered as {typeof(TValue)}.",
                nameof(column));

        CollectionLevels = (RowApiCollectionLevel[])collectionLevels.Clone();
    }

    internal RowApiNestedLeafBuffer<TValue> CreateBuffer(int rowCount)
        => new(Column, CollectionLevels[0].DefinedDefinitionLevel, rowCount);

    internal override RowApiColumnReadState CreateState()
        => new RowApiNestedColumnReadState<TValue>(this);

    internal override RowApiColumnWriteState CreateWriteState(RowGroupWriter rowGroupWriter, int rowCount)
        => new RowApiNestedColumnWriteState<TValue>(this, rowCount)
        {
            Serialized = rowGroupWriter.CreateSerializedColumn<TValue>(Column)
        };

    internal override RowApiColumnWriteState CreateWriteState(int rowCount)
        => new RowApiNestedColumnWriteState<TValue>(this, rowCount);

    internal override RowApiColumnWriteState CreateWriteState(ParquetWriter writer, int rowCount)
        => new RowApiNestedColumnWriteState<TValue>(this, rowCount)
        {
            Serialized = writer.CreateSerializedColumn<TValue>(Column)
        };

    static bool IsPhysicalType(ParquetPhysicalType physicalType)
        => physicalType switch
        {
            ParquetPhysicalType.Boolean => typeof(TValue) == typeof(bool),
            ParquetPhysicalType.Int32 => typeof(TValue) == typeof(int),
            ParquetPhysicalType.Int64 => typeof(TValue) == typeof(long),
            ParquetPhysicalType.Float => typeof(TValue) == typeof(float),
            ParquetPhysicalType.Double => typeof(TValue) == typeof(double),
            ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96
                => typeof(TValue) == typeof(byte[]),
            _ => false
        };
}
