using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

public abstract class RowApiColumnDescriptor
{
    protected RowApiColumnDescriptor(string propertyName, Column column, ulong projectionBit)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        ArgumentNullException.ThrowIfNull(column);

        PropertyName = propertyName;
        Column = column;
        ProjectionBit = projectionBit;
    }

    public string PropertyName { get; }

    public Column Column { get; }

    public ulong ProjectionBit { get; }

    internal abstract RowApiColumnReadState CreateState();

    internal abstract RowApiColumnWriteState CreateWriteState(RowGroupWriter rowGroupWriter, int rowCount);

    internal abstract RowApiColumnWriteState CreateWriteState(ParquetWriter writer, int rowCount);
}
