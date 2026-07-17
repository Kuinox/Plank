using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Writing;

namespace Plank.Reading.Logical;

public readonly struct ColumnBuffer<T>
{
    readonly ParquetBuffer _nativeValues;
    readonly ReadOnlyMemory<T> _managedValues;
    readonly int _valueCount;

    internal ColumnBuffer(ReadOnlyMemory<T> values)
    {
        _nativeValues = default;
        _managedValues = values;
        _valueCount = values.Length;
        CanRetain = false;
    }

    internal ColumnBuffer(ParquetBuffer values, int valueCount)
    {
        _nativeValues = values;
        _managedValues = default;
        _valueCount = valueCount;
        CanRetain = true;
    }

    public ReadOnlySpan<T> Values
        => CanRetain ? ParquetBuffer.AsReadOnlySpan<T>(_nativeValues, _valueCount) : _managedValues.Span;

    public bool CanRetain { get; }

    public ParquetBuffer Retain()
    {
        if (!CanRetain)
            throw new NotSupportedException(
                $"Buffers containing {typeof(T)} values do not use retainable unmanaged storage yet.");
        if (_valueCount == 0)
            return default;
        return _nativeValues.RetainSlice(0, checked(_valueCount * Unsafe.SizeOf<T>()));
    }

    internal int ValueCount
        => _valueCount;

    internal Span<T> WritableValues
    {
        get
        {
            if (CanRetain)
                return ParquetBuffer.AsSpan<T>(_nativeValues, _valueCount);
            if (MemoryMarshal.TryGetArray(_managedValues, out var segment) && segment.Array is not null)
                return segment.Array.AsSpan(segment.Offset, segment.Count);
            throw new InvalidOperationException("Managed buffer values are not array-backed.");
        }
    }
}
