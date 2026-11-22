using Plank.Writing;

namespace Plank.RowApi;

abstract class RowApiColumnWriteState
{
    internal abstract ulong? FixedValueSizeBytes { get; }

    internal abstract void Bind(ParquetWriter writer);

    internal abstract void Unbind();

    internal abstract void Serialize(int count);

    internal abstract void Write(RowGroupWriter rowGroupWriter);

    internal abstract void ResetForReuse(int start, int count);

    /// <summary>Clears every row before the slot is reused.</summary>
    internal virtual void ResetSlot(int count)
        => ResetForReuse(0, count);

    internal abstract ulong GetValueSize(int index);

    internal abstract void Resize(int rowCount);

    internal abstract void CopyValueTo(int sourceIndex, RowApiColumnWriteState destination, int destinationIndex);
}
