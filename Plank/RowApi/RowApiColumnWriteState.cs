using Plank.Writing;

namespace Plank.RowApi;

abstract class RowApiColumnWriteState
{
    internal abstract void Serialize(int count);

    internal abstract void Write(RowGroupWriter rowGroupWriter);

    internal abstract void ResetForReuse(int count);
}
