using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

/// <summary>
/// Describes a strongly typed row API column to the generated row reader and writer infrastructure.
/// </summary>
/// <typeparam name="T">The column's generated CLR value type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public sealed class RowApiColumnDescriptor<T> : RowApiColumnDescriptor
{
    /// <summary>Initializes a strongly typed descriptor for a generated row property.</summary>
    /// <param name="propertyName">The generated row property's name.</param>
    /// <param name="column">The corresponding Parquet column.</param>
    public RowApiColumnDescriptor(string propertyName, LeafColumn column)
        : base(propertyName, column)
    {
    }

    internal override RowApiColumnReadState CreateState()
    {
        if (typeof(T) == typeof(byte[]) ||
            typeof(T) == typeof(ReadOnlyMemory<byte>) ||
            typeof(T) == typeof(ReadOnlyMemory<byte>?))
            return new RowApiBinaryColumnReadState(this,
                missingIsNull: typeof(T) != typeof(ReadOnlyMemory<byte>));
        return new RowApiColumnReadState<T>(this);
    }

    internal override RowApiColumnWriteState CreateWriteState(RowGroupWriter rowGroupWriter, int rowCount)
        => new RowApiColumnWriteState<T>(this, rowGroupWriter, rowCount);

    internal override RowApiColumnWriteState CreateWriteState(ParquetWriter writer, int rowCount)
        => new RowApiColumnWriteState<T>(this, writer, rowCount);
}
