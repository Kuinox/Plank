using Plank.Writing;

namespace Plank.RowApi;

abstract class RowApiColumnWriteState
{
    internal abstract void Bind(ParquetWriter writer);

    internal abstract void Unbind();

    internal abstract void Serialize(int count);

    internal abstract void Write(RowGroupWriter rowGroupWriter);

    internal abstract void ResetForReuse(int start, int count);

    internal abstract ulong GetValueSize(int index);

    internal abstract void Resize(int rowCount);

    internal abstract void CopyValueTo(int sourceIndex, RowApiColumnWriteState destination, int destinationIndex);
}
