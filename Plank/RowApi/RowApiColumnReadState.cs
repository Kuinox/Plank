using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.RowApi;

abstract class RowApiColumnReadState : IDisposable
{
    protected RowApiColumnReadState(RowApiColumnDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Descriptor = descriptor;
        PropertyName = descriptor.PropertyName;
        Column = descriptor.Column.Column;
        Ordinal = -1;
        Projected = false;
        Materialized = false;
        CurrentIndex = -1;
    }

    internal readonly RowApiColumnDescriptor Descriptor;

    internal readonly string PropertyName;

    internal readonly Column Column;

    internal int Ordinal;

    internal bool Projected;

    internal bool Materialized;

    internal int CurrentIndex;

    internal void ResetForProjection(bool projected)
    {
        Projected = projected;
        Materialized = false;
        Ordinal = -1;
        ResetBufferState();
    }

    internal void ResetForMissingMaterialized()
    {
        Projected = false;
        Materialized = true;
        Ordinal = -1;
        SetMissingValue();
    }

    internal void ResetForMissingUnprojected()
    {
        Projected = false;
        Materialized = false;
        Ordinal = -1;
        ResetBufferState();
    }

    internal abstract void ResetBufferState();

    internal abstract void SetMissingValue();

    internal abstract void Open(RowGroup rowGroup);

    internal abstract void Advance();

    internal abstract void DisposeBuffers();

    public void Dispose()
        => DisposeBuffers();
}
