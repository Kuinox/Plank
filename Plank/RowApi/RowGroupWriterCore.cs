using Plank.Writing;

namespace Plank.RowApi;

public sealed class RowGroupWriterCore<TSlot>
    where TSlot : RowBufferSlot
{
    readonly RowGroupWriter _rowGroupWriter;
    readonly TSlot _slot;
    bool _written;

    public RowGroupWriterCore(RowGroupWriter rowGroupWriter, TSlot slot)
    {
        _rowGroupWriter = rowGroupWriter ?? throw new ArgumentNullException(nameof(rowGroupWriter));
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _written = false;
    }

    public TSlot GetSlotForRow()
    {
        ThrowIfWritten("Rows are already written for this row group.");
        return _slot;
    }

    public void Next()
    {
        ThrowIfWritten("Rows are already written for this row group.");
        _slot.Next();
    }

    public void Write()
    {
        ThrowIfWritten("This row writer was already written.");
        _slot.SerializeColumns();
        _slot.WriteSerialized(_rowGroupWriter);
        _written = true;
    }

    void ThrowIfWritten(string message)
    {
        if (_written)
            throw new InvalidOperationException(message);
    }
}
