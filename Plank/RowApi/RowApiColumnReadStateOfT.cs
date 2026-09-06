using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Reading.Logical;

namespace Plank.RowApi;

sealed class RowApiColumnReadState<T> : RowApiColumnReadState
{
    static readonly RuntimeTypeHandle ValueType = typeof(T).TypeHandle;

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
        BufferedValueCount = 0;
        _buffersOpen = false;
    }

    internal Span<T> CurrentSpan
        => _usingMissing
            ? MemoryMarshal.CreateSpan(ref _missing, 1)
            : _buffer.ValidatedWritableValues;

    internal override void ResetBufferState()
    {
        DisposeBuffers();
        _buffer = default;
        _usingMissing = false;
        CurrentIndex = -1;
        BufferedValueCount = 0;
    }

    internal override void SetMissingValue()
    {
        DisposeBuffers();
        _missing = default!;
        _usingMissing = true;
        CurrentIndex = 0;
        BufferedValueCount = 1;
    }

    internal override void Open(RowGroup rowGroup)
    {
        DisposeBuffers();
        RowGroup.ValidatePhysicalType<T>(Column);
        _buffers = new RowGroupColumn<T>.Enumerator(
            rowGroup.EnumerateBuffers<T>(Definition, Ordinal).GetEnumerator(), rowGroup);
        _buffersOpen = true;
        _buffer = default;
        _usingMissing = false;
        CurrentIndex = -1;
        BufferedValueCount = 0;
    }

    internal override unsafe RowApiValueBatch GetValueBatch()
    {
        if (!Projected || _usingMissing)
            return default;
        var values = CurrentSpan[CurrentIndex..];
        return new RowApiValueBatch((nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(values)),
            ValueType);
    }

    internal override bool SupportsBatchAdvance
        => true;

    internal override int PrepareBatch(int consumedRows)
    {
        // Missing columns expose one synthetic default value for every row. They are
        // normally excluded from the advancing-state list, but keeping this method
        // total prevents a future caller from exhausting that synthetic value.
        if (_usingMissing)
            return int.MaxValue;

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

    internal override void AdvanceBuffer()
    {
        // Do not retain a view of a previous buffer if advancing or validation fails.
        _buffer = default;
        while (true)
        {
            if (!_buffers.MoveNext())
                throw new CorruptParquetException($"Column '{PropertyName}' ended before the row group was complete.");

            var buffer = _buffers.Current;
            _ = buffer.WritableValues;
            _buffer = buffer;
            BufferedValueCount = _buffer.ValueCount;
            CurrentIndex = 0;
            if (BufferedValueCount != 0)
                return;
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
