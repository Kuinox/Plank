using System.Runtime.CompilerServices;
using Plank.Writing;

namespace Plank.RowApi;

sealed class RowApiColumnWriteState<T> : RowApiColumnWriteState
{
    readonly RowApiColumnDescriptor _descriptor;
    RowApiColumnBuffer<T> _buffer;
    int _segmentIndex;
    SerializedColumn<T>? _serialized;

    internal RowApiColumnWriteState(RowApiColumnDescriptor descriptor, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        _descriptor = descriptor;
        FixedValueSizeBytes = RowValueSizeEstimator.TryGetFixedSize<T>(descriptor.Column.Column, out var size)
            ? size
            : null;
        _buffer = new RowApiColumnBuffer<T>(rowCount, 1);
        _segmentIndex = 0;
    }

    internal RowApiColumnWriteState(RowApiColumnDescriptor descriptor, RowGroupWriter rowGroupWriter, int rowCount)
        : this(descriptor, rowCount)
    {
        ArgumentNullException.ThrowIfNull(rowGroupWriter);

        _serialized = rowGroupWriter.CreateSerializedColumn<T>(descriptor.Column);
    }

    internal RowApiColumnWriteState(RowApiColumnDescriptor descriptor, ParquetWriter writer, int rowCount)
        : this(descriptor, rowCount)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _serialized = writer.CreateSerializedColumn<T>(descriptor.Column);
    }

    internal T[] Values
        => _buffer.Values;

    internal int Offset
        => _segmentIndex * _buffer.Stride;

    internal override Type ValueType
        => typeof(T);

    internal override ulong? FixedValueSizeBytes { get; }

    internal override void ShareBuffer(RowApiColumnWriteState[] states)
    {
        var shared = new RowApiColumnBuffer<T>(_buffer.Capacity, states.Length);
        for (var i = 0; i < states.Length; i++)
        {
            if (states[i] is not RowApiColumnWriteState<T> state)
                throw new InvalidOperationException("Generated row buffer types do not match.");
            state._buffer = shared;
            state._segmentIndex = i;
        }
    }

    internal override void Bind(ParquetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (_serialized is not null)
            throw new InvalidOperationException("The row API column is already bound to a writer.");
        _serialized = writer.CreateSerializedColumn<T>(_descriptor.Column);
    }

    internal override void Unbind()
        => _serialized = null;

    internal override void Serialize(int count)
        => GetSerialized().Serialize(new ReadOnlySpan<T>(Values, Offset, count));

    internal override void Write(RowGroupWriter rowGroupWriter)
        => rowGroupWriter.Write(GetSerialized());

    internal override void ResetForReuse(int start, int count)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(Values, Offset + start, count);
    }

    internal override ulong GetValueSize(int index)
        => RowValueSizeEstimator.Estimate(Values[Offset + index], _descriptor.Column.Column);

    internal override void Resize(int rowCount)
        => _buffer.Resize(rowCount);

    internal override void CopyValueTo(int sourceIndex, RowApiColumnWriteState destination, int destinationIndex)
    {
        if (destination is not RowApiColumnWriteState<T> target)
            throw new InvalidOperationException("Row API column buffer types do not match.");
        target.Values[target.Offset + destinationIndex] = Values[Offset + sourceIndex];
    }

    SerializedColumn<T> GetSerialized()
        => _serialized ?? throw new InvalidOperationException("The row API column is not bound to a writer.");
}

sealed class RowApiColumnBuffer<T>
{
    // A non-power-of-two gap prevents equally sized column streams from repeatedly
    // mapping to the same cache sets when callers write a wide row.
    const int SegmentPadding = 257;

    readonly int _segmentCount;

    internal RowApiColumnBuffer(int capacity, int segmentCount)
    {
        _segmentCount = segmentCount;
        Capacity = capacity;
        Stride = GetStride(capacity, segmentCount);
        Values = Allocate(capacity, segmentCount, Stride);
    }

    internal int Capacity { get; private set; }

    internal int Stride { get; private set; }

    internal T[] Values { get; private set; }

    internal void Resize(int capacity)
    {
        if (capacity == Capacity)
            return;

        var stride = GetStride(capacity, _segmentCount);
        var values = Allocate(capacity, _segmentCount, stride);
        var copyCount = Math.Min(Capacity, capacity);
        for (var i = 0; i < _segmentCount; i++)
            Array.Copy(Values, i * Stride, values, i * stride, copyCount);
        Values = values;
        Capacity = capacity;
        Stride = stride;
    }

    static int GetStride(int capacity, int segmentCount)
    {
        if (capacity == 0 || segmentCount == 1)
            return capacity;
        var padding = capacity <= SegmentPadding * 4 ? 1 : SegmentPadding;
        return checked(capacity + padding);
    }

    static T[] Allocate(int capacity, int segmentCount, int stride)
    {
        if (capacity == 0)
            return [];
        var length = segmentCount == 1
            ? capacity
            : checked(segmentCount * stride);
        return new T[length];
    }
}
