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
        Definition = descriptor.Column;
        Column = descriptor.Column.Column;
        Ordinal = -1;
        Projected = false;
        Materialized = false;
        CurrentIndex = -1;
        BufferedValueCount = 0;
    }

    internal readonly RowApiColumnDescriptor Descriptor;

    internal readonly string PropertyName;

    internal readonly LeafColumn Definition;

    internal readonly Column Column;

    internal int Ordinal;

    internal bool Projected;

    internal bool Materialized;

    internal bool Prefetched;

    internal int CurrentIndex;

    internal int BufferedValueCount;

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

    internal virtual RowApiValueBatch GetValueBatch() => default;

    internal virtual bool SupportsBatchAdvance
        => false;

    internal virtual int PrepareBatch(int consumedRows)
        => throw new InvalidOperationException($"Column '{PropertyName}' does not support batched row advancement.");

    internal int PrepareValueBatch(int consumedRows)
    {
        if (CurrentIndex < 0)
        {
            TakeNextBuffer();
        }
        else
        {
            CurrentIndex = checked(CurrentIndex + consumedRows);
            if (CurrentIndex == BufferedValueCount)
                TakeNextBuffer();
            else if ((uint)CurrentIndex > (uint)BufferedValueCount)
                throw new CorruptParquetException(
                    $"Column '{PropertyName}' advanced beyond its current value buffer.");
        }

        return BufferedValueCount - CurrentIndex;
    }

    internal void Advance()
    {
        CurrentIndex++;
        if ((uint)CurrentIndex < (uint)BufferedValueCount)
        {
            Prefetched = false;
            return;
        }
        TakeNextBuffer();
    }

    internal void TakeNextBuffer()
    {
        if (Prefetched)
        {
            Prefetched = false;
            CurrentIndex = 0;
        }
        else
            AdvanceBuffer();
    }

    internal abstract void AdvanceBuffer();

    internal abstract void DisposeBuffers();

    public void Dispose()
        => DisposeBuffers();
}

readonly struct RowApiValueBatch(nint address, Type type)
{
    internal readonly nint Address = address;
    internal readonly Type Type = type;
}
