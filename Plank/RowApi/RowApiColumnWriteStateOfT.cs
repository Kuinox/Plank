using System.Runtime.CompilerServices;
using Plank.Writing;

namespace Plank.RowApi;

sealed class RowApiColumnWriteState<T> : RowApiColumnWriteState
{
    readonly SerializedColumn<T> _serialized;

    internal RowApiColumnWriteState(RowApiColumnDescriptor<T> descriptor, RowGroupWriter rowGroupWriter, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(rowGroupWriter);

        Values = rowCount == 0 ? [] : new T[rowCount];
        _serialized = rowGroupWriter.CreateSerializedColumn<T>(descriptor.Column);
    }

    internal RowApiColumnWriteState(RowApiColumnDescriptor<T> descriptor, ParquetWriter writer, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(writer);

        Values = rowCount == 0 ? [] : new T[rowCount];
        _serialized = writer.CreateSerializedColumn<T>(descriptor.Column);
    }

    internal T[] Values;

    internal override void Serialize(int count)
        => _serialized.Serialize(new ReadOnlySpan<T>(Values, 0, count));

    internal override void Write(RowGroupWriter rowGroupWriter)
        => rowGroupWriter.Write(_serialized);

    internal override void ResetForReuse(int count)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(Values, 0, count);
    }
}
