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
        Column = descriptor.Column;
        ProjectionBit = descriptor.ProjectionBit;
        Ordinal = -1;
        Projected = false;
        Materialized = false;
        CurrentIndex = -1;
    }

    internal readonly RowApiColumnDescriptor Descriptor;

    internal readonly string PropertyName;

    internal readonly Column Column;

    internal readonly ulong ProjectionBit;

    internal int Ordinal;

    internal bool Projected;

    internal bool Materialized;

    internal int CurrentIndex;

    internal void ResetForProjection(ulong projection)
    {
        Projected = (projection & ProjectionBit) != 0;
        Materialized = false;
        Ordinal = -1;
        ResetPageState();
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
        ResetPageState();
    }

    internal abstract void ResetPageState();

    internal abstract void SetMissingValue();

    internal abstract void Open(RowGroupReader rowGroup);

    internal abstract void Advance();

    internal abstract void DisposePages();

    public void Dispose()
        => DisposePages();
}
