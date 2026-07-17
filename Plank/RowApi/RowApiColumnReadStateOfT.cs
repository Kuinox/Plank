using Plank.Reading.Logical;

namespace Plank.RowApi;

sealed class RowApiColumnReadState<T> : RowApiColumnReadState
{
    readonly T[] _missing;
    RowGroupColumn<T>.Enumerator _buffers;
    ColumnBuffer<T> _buffer;
    bool _buffersOpen;

    internal RowApiColumnReadState(RowApiColumnDescriptor<T> descriptor)
        : base(descriptor)
    {
        _missing = [default!];
        _buffers = default;
        _buffer = default;
        CurrentIndex = -1;
        _buffersOpen = false;
    }

    internal Span<T> CurrentSpan
        => _buffer.WritableValues;

    internal override void ResetBufferState()
    {
        DisposeBuffers();
        _buffer = default;
        CurrentIndex = -1;
    }

    internal override void SetMissingValue()
    {
        DisposeBuffers();
        _buffer = new ColumnBuffer<T>(_missing);
        CurrentIndex = 0;
    }

    internal override void Open(RowGroup rowGroup)
    {
        DisposeBuffers();
        _buffers = rowGroup.Column<T>(Ordinal).GetEnumerator();
        _buffersOpen = true;
        _buffer = default;
        CurrentIndex = -1;
    }

    internal override void Advance()
    {
        if (!Projected)
            return;

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
