using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

/// <summary>
/// Describes a row API column to the generated row reader and writer infrastructure.
/// </summary>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public abstract class RowApiColumnDescriptor
{
    /// <summary>Initializes a descriptor for a generated row property.</summary>
    /// <param name="propertyName">The generated row property's name.</param>
    /// <param name="column">The corresponding Parquet column.</param>
    protected RowApiColumnDescriptor(string propertyName, LeafColumn column)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        ArgumentNullException.ThrowIfNull(column);

        PropertyName = propertyName;
        Column = column;
    }

    /// <summary>Gets the generated row property's name.</summary>
    public string PropertyName { get; }

    /// <summary>Gets the corresponding Parquet column.</summary>
    public LeafColumn Column { get; }

    internal abstract RowApiColumnReadState CreateState();

    internal abstract RowApiColumnWriteState CreateWriteState(RowGroupWriter rowGroupWriter, int rowCount);

    internal abstract RowApiColumnWriteState CreateWriteState(ParquetWriter writer, int rowCount);
}
