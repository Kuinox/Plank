using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Reading.Logical;

namespace Plank.RowApi;

sealed class RowApiColumnReadState<T> : RowApiColumnReadState
{
    RowGroupColumn<T>.Enumerator _buffers;
    ColumnBuffer<T> _buffer;
    T _missing;
    bool _usingMissing;
    bool _buffersOpen;

    internal RowApiColumnReadState(RowApiColumnDescriptor<T> descriptor)
        : base(descriptor)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new NotSupportedException(
                $"Row reader values containing {typeof(T)} require the variable-length byte state.");

        _buffers = default;
        _buffer = default;
        _missing = default!;
        _usingMissing = false;
        CurrentIndex = -1;
        _buffersOpen = false;
    }

    internal Span<T> CurrentSpan
        => _usingMissing
            ? MemoryMarshal.CreateSpan(ref _missing, 1)
            : _buffer.WritableValues;

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
        _missing = default!;
        _usingMissing = true;
        CurrentIndex = 0;
    }

    internal override void Open(RowGroup rowGroup)
    {
        DisposeBuffers();
        RowGroup.ValidatePhysicalType<T>(Column);
        _buffers = new RowGroupColumn<T>.Enumerator(
            rowGroup.EnumerateBuffers<T>(Definition, Ordinal).GetEnumerator());
        _buffersOpen = true;
        _buffer = default;
        _usingMissing = false;
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
