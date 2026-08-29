using System.Runtime.CompilerServices;
using Plank.Writing;

namespace Plank.RowApi;

sealed class RowApiColumnWriteState<T> : RowApiColumnWriteState
{
    readonly RowApiColumnDescriptor _descriptor;
    SerializedColumn<T>? _serialized;

    internal RowApiColumnWriteState(RowApiColumnDescriptor descriptor, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        _descriptor = descriptor;
        FixedValueSizeBytes = RowValueSizeEstimator.TryGetFixedSize<T>(descriptor.Column.Column, out var size)
            ? size
            : null;
        Values = rowCount == 0 ? [] : new T[rowCount];
    }

    internal RowApiColumnWriteState(RowApiColumnDescriptor descriptor, RowGroupWriter rowGroupWriter, int rowCount)
        : this(descriptor, rowCount)
    {
        ArgumentNullException.ThrowIfNull(rowGroupWriter);

        _serialized = rowGroupWriter.CreateSerializedColumn<T>(descriptor.Column);
    }

    internal RowApiColumnWriteState(RowApiColumnDescriptor descriptor, ParquetWriter writer, int rowCount)
        : this(descriptor, rowCount)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _serialized = writer.CreateSerializedColumn<T>(descriptor.Column);
    }

    internal T[] Values;

    internal override ulong? FixedValueSizeBytes { get; }

    internal override void Bind(ParquetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (_serialized is not null)
            throw new InvalidOperationException("The row API column is already bound to a writer.");
        _serialized = writer.CreateSerializedColumn<T>(_descriptor.Column);
    }

    internal override void Unbind()
        => _serialized = null;

    internal override void Serialize(int count)
        => GetSerialized().Serialize(new ReadOnlySpan<T>(Values, 0, count));

    internal override void Write(RowGroupWriter rowGroupWriter)
        => rowGroupWriter.Write(GetSerialized());

    internal override void ResetForReuse(int start, int count)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(Values, start, count);
    }

    internal override ulong GetValueSize(int index)
        => RowValueSizeEstimator.Estimate(Values[index], _descriptor.Column.Column);

    internal override void Resize(int rowCount)
        => Array.Resize(ref Values, rowCount);

    internal override void CopyValueTo(int sourceIndex, RowApiColumnWriteState destination, int destinationIndex)
    {
        if (destination is not RowApiColumnWriteState<T> target)
            throw new InvalidOperationException("Row API column buffer types do not match.");
        target.Values[destinationIndex] = Values[sourceIndex];
    }

    SerializedColumn<T> GetSerialized()
        => _serialized ?? throw new InvalidOperationException("The row API column is not bound to a writer.");
}
