using Plank.Reading.Logical;

namespace Plank.RowApi;

sealed class RowApiBinaryColumnReadState : RowApiColumnReadState
{
    readonly bool _missingIsNull;
    RowGroupColumn<byte>.Enumerator _buffers;
    ColumnBuffer<byte> _buffer;
    bool _usingMissing;
    bool _buffersOpen;

    internal RowApiBinaryColumnReadState(RowApiColumnDescriptor descriptor, bool missingIsNull)
        : base(descriptor)
    {
        _missingIsNull = missingIsNull;
        _buffers = default;
        _buffer = default;
        _usingMissing = false;
        CurrentIndex = -1;
        _buffersOpen = false;
    }

    internal ReadOnlySpan<byte> CurrentValue
        => _usingMissing || CurrentIndex < 0 ? [] : _buffer.GetValue(CurrentIndex);

    internal bool CurrentIsNull
        => _usingMissing ? _missingIsNull : CurrentIndex >= 0 && _buffer.IsNull(CurrentIndex);

    internal override void ResetBufferState()
    {
        DisposeBuffers();
        _buffer = default;
        _usingMissing = false;
        CurrentIndex = -1;
    }

    internal override void SetMissingValue()
    {
        DisposeBuffers();
        _buffer = default;
        _usingMissing = true;
        CurrentIndex = 0;
    }

    internal override void Open(RowGroup rowGroup)
    {
        DisposeBuffers();
        _buffers = rowGroup.Column<byte>(Ordinal).GetEnumerator();
        _buffersOpen = true;
        _buffer = default;
        _usingMissing = false;
        CurrentIndex = -1;
    }

    internal override void Advance()
    {
        CurrentIndex++;
        while ((uint)CurrentIndex >= (uint)_buffer.ValueCount)
        {
            if (!_buffers.MoveNext())
                throw new InvalidDataException($"Column '{PropertyName}' ended before the row group was complete.");

            _buffer = _buffers.Current;
            CurrentIndex = 0;
            if (_buffer.ValueCount == 0)
                CurrentIndex = -1;
        }
    }

    internal override void DisposeBuffers()
    {
        if (!_buffersOpen)
            return;

        _buffers.Dispose();
        _buffersOpen = false;
    }
}
