using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

public sealed class RowApiColumnDescriptor<T> : RowApiColumnDescriptor
{
    public RowApiColumnDescriptor(string propertyName, Column column, ulong projectionBit)
        : base(propertyName, column, projectionBit)
    {
    }

    internal override RowApiColumnReadState CreateState()
        => new RowApiColumnReadState<T>(this);

    internal override RowApiColumnWriteState CreateWriteState(RowGroupWriter rowGroupWriter, int rowCount)
        => new RowApiColumnWriteState<T>(this, rowGroupWriter, rowCount);

    internal override RowApiColumnWriteState CreateWriteState(ParquetWriter writer, int rowCount)
        => new RowApiColumnWriteState<T>(this, writer, rowCount);
}
