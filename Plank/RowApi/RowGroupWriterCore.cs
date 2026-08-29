using Plank.Writing;

namespace Plank.RowApi;

/// <summary>Coordinates one generated row buffer with a row-group writer.</summary>
/// <typeparam name="TSlot">The generated buffer-slot type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public sealed class RowGroupWriterCore<TSlot>
    where TSlot : RowBufferSlot
{
    readonly RowGroupWriter _rowGroupWriter;
    readonly TSlot _slot;
    bool _rowPending;
    bool _written;

    /// <summary>Initializes the core used by a generated row-group writer.</summary>
    /// <param name="rowGroupWriter">The destination row-group writer.</param>
    /// <param name="slot">The generated row buffer.</param>
    public RowGroupWriterCore(RowGroupWriter rowGroupWriter, TSlot slot)
    {
        _rowGroupWriter = rowGroupWriter ?? throw new ArgumentNullException(nameof(rowGroupWriter));
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _rowPending = false;
        _written = false;
    }

    /// <summary>Gets the buffer slot for generated row assignment.</summary>
    /// <returns>The writable buffer slot.</returns>
    public TSlot GetSlotForRow()
    {
        ThrowIfWritten("Rows are already written for this row group.");
        if (_rowPending)
            _slot.Next();
        else
            _rowPending = true;
        return _slot;
    }

    /// <summary>Advances the generated writer to its next row.</summary>
    public void Next()
    {
        ThrowIfWritten("Rows are already written for this row group.");
        _slot.Next();
        _rowPending = false;
    }

    /// <summary>Serializes and writes the generated row buffer.</summary>
    public void Write()
    {
        ThrowIfWritten("This row writer was already written.");
        if (_rowPending)
        {
            _slot.Next();
            _rowPending = false;
        }
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
