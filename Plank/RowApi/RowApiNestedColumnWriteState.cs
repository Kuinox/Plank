using Plank.Writing;

namespace Plank.RowApi;

sealed class RowApiNestedColumnWriteState<TValue>(RowApiNestedColumnDescriptor<TValue> descriptor, int rowCount)
    : RowApiColumnWriteState
{
    internal readonly RowApiNestedLeafBuffer<TValue> Buffer = descriptor.CreateBuffer(rowCount);

    internal SerializedColumn<TValue>? Serialized;

    internal override ulong? FixedValueSizeBytes => null;

    internal override void Bind(ParquetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Serialized is not null)
            throw new InvalidOperationException("The row API column is already bound to a writer.");
        Serialized = writer.CreateSerializedColumn<TValue>(descriptor.Column);
    }

    internal override void Unbind()
        => Serialized = null;

    internal override void Serialize(int count)
    {
        var serialized = Serialized ?? throw new InvalidOperationException("The row API column is not bound to a writer.");
        Buffer.GetRows(count, out var repetitionLevels, out var definitionLevels, out var values);
        serialized.SerializeNestedLevels(repetitionLevels, definitionLevels, values, count);
    }

    internal override void Write(RowGroupWriter rowGroupWriter)
        => rowGroupWriter.Write(Serialized ?? throw new InvalidOperationException("The row API column is not bound to a writer."));

    internal override void ResetForReuse(int start, int count)
        => Buffer.ClearRows(start, count);

    internal override void ResetSlot(int count)
        => Buffer.Reset();

    internal override ulong GetValueSize(int index)
        => Buffer.EstimateRowSize(index);

    internal override void Resize(int rowCount)
        => Buffer.Resize(rowCount);

    internal override void CopyValueTo(int sourceIndex, RowApiColumnWriteState destination, int destinationIndex)
    {
        if (destination is not RowApiNestedColumnWriteState<TValue> target)
            throw new InvalidOperationException("Row API column buffer types do not match.");
        Buffer.CopyRowTo(sourceIndex, target.Buffer, destinationIndex);
    }
}
