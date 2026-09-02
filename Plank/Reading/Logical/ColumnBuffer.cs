using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Reading.Logical.Internal;

namespace Plank.Reading.Logical;

public readonly struct ColumnBuffer<T>
{
    readonly ParquetBuffer _nativeValues;
    readonly ReadOnlyMemory<byte> _borrowedValues;
    readonly IParquetBufferPool? _borrowedBufferPool;
    readonly nint _variableLengthPayloadAddress;
    readonly int _valueCount;
    readonly bool _isVariableLength;

    internal ColumnBuffer(ParquetBuffer values, int valueCount)
    {
        _nativeValues = values;
        _borrowedValues = default;
        _borrowedBufferPool = null;
        _variableLengthPayloadAddress = 0;
        _valueCount = valueCount;
        _isVariableLength = false;
    }

    internal ColumnBuffer(ParquetBuffer values, int valueCount, bool isVariableLength)
    {
        if (!isVariableLength || typeof(T) != typeof(byte))
            throw new ArgumentException("Variable-length buffers must contain bytes.",
                nameof(isVariableLength));

        _nativeValues = values;
        _borrowedValues = default;
        _borrowedBufferPool = null;
        _variableLengthPayloadAddress = valueCount == 0
            ? 0
            : values.DangerousGetAddress() + checked(valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());
        _valueCount = valueCount;
        _isVariableLength = true;
    }

    internal ColumnBuffer(ReadOnlyMemory<byte> borrowedValues, int valueCount,
        IParquetBufferPool borrowedBufferPool)
    {
        ArgumentNullException.ThrowIfNull(borrowedBufferPool);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"{typeof(T)} cannot be projected over borrowed byte storage.");
        var byteLength = checked(valueCount * Unsafe.SizeOf<T>());
        if (valueCount <= 0 || borrowedValues.Length < byteLength)
            throw new ArgumentOutOfRangeException(nameof(valueCount));

        _nativeValues = default;
        _borrowedValues = borrowedValues[..byteLength];
        _borrowedBufferPool = borrowedBufferPool;
        _variableLengthPayloadAddress = 0;
        _valueCount = valueCount;
        _isVariableLength = false;
    }

    public ReadOnlySpan<T> Values
        => _isVariableLength
            ? ProjectBytes(GetVariableLengthPayload())
            : _borrowedValues.IsEmpty
                ? ParquetBuffer.AsReadOnlySpan<T>(_nativeValues, _valueCount)
                : ProjectBorrowedValues(_borrowedValues.Span, _valueCount);

    public int Count
        => _valueCount;

    public bool IsNull(int index)
    {
        ValidateIndex(index);
        return _isVariableLength
            ? GetVariableLengthDescriptor(index).IsNull
            : Values[index] is null;
    }

    public ReadOnlySpan<T> GetValue(int index)
    {
        ValidateIndex(index);
        return _isVariableLength
            ? ProjectBytes(GetVariableLengthValue(index))
            : Values.Slice(index, 1);
    }

    public ParquetBuffer Retain()
    {
        if (_valueCount == 0)
            return default;
        var byteLength = _isVariableLength
            ? checked(_valueCount * Unsafe.SizeOf<BinaryValueDescriptor>())
            : checked(_valueCount * Unsafe.SizeOf<T>());
        if (!_borrowedValues.IsEmpty)
        {
            using var rented = _borrowedBufferPool!.Rent(checked((uint)byteLength));
            _borrowedValues.Span[..byteLength].CopyTo(rented.Span);
            return rented.RetainSlice(0, byteLength);
        }
        return _nativeValues.RetainSlice(0, byteLength);
    }

    /// <summary>Retains the storage for one value independently of this buffer's enumerator.</summary>
    /// <param name="index">The zero-based value index.</param>
    /// <returns>
    /// A reference-counted buffer containing the selected value, or an empty buffer when the value is null.
    /// </returns>
    /// <remarks>
    /// For variable-length byte columns, the returned span is exactly the selected byte value rather than the
    /// page's descriptor table and combined payload. Dispose the returned buffer when it is no longer needed.
    /// </remarks>
    public ParquetBuffer RetainValue(int index)
    {
        ValidateIndex(index);
        if (_isVariableLength)
        {
            var descriptor = GetVariableLengthDescriptor(index);
            if (descriptor.IsNull)
                return default;
            var descriptorByteLength = checked(_valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());
            return _nativeValues.RetainSlice(
                checked(descriptorByteLength + descriptor.Offset), descriptor.Length);
        }

        if (Values[index] is null)
            return default;

        var elementByteLength = Unsafe.SizeOf<T>();
        var byteOffset = checked(index * elementByteLength);
        if (!_borrowedValues.IsEmpty)
        {
            using var rented = _borrowedBufferPool!.Rent(checked((uint)elementByteLength));
            _borrowedValues.Span.Slice(byteOffset, elementByteLength).CopyTo(rented.Span);
            return rented.RetainSlice(0, elementByteLength);
        }
        return _nativeValues.RetainSlice(byteOffset, elementByteLength);
    }

    internal int ValueCount
        => _valueCount;

    internal ParquetBuffer NativeValues
        => _nativeValues;

    internal Span<T> WritableValues
    {
        get
        {
            if (_isVariableLength)
                throw new InvalidOperationException("Variable-length buffers are not writable.");
            if (!_borrowedValues.IsEmpty)
                throw new InvalidOperationException("Borrowed buffers are not writable.");
            return ParquetBuffer.AsSpan<T>(_nativeValues, _valueCount);
        }
    }

    static ReadOnlySpan<T> ProjectBorrowedValues(ReadOnlySpan<byte> bytes, int valueCount)
        => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(bytes)), valueCount);

    BinaryValueDescriptor GetVariableLengthDescriptor(int index)
        => ParquetBuffer.AsReadOnlySpan<BinaryValueDescriptor>(_nativeValues, _valueCount)[index];

    ReadOnlySpan<byte> GetVariableLengthValue(int index)
    {
        var descriptor = GetVariableLengthDescriptor(index);
        return descriptor.GetSpan(_variableLengthPayloadAddress);
    }

    ReadOnlySpan<byte> GetVariableLengthPayload()
    {
        var descriptors = ParquetBuffer.AsReadOnlySpan<BinaryValueDescriptor>(_nativeValues, _valueCount);
        var payloadByteLength = 0;
        for (var i = 0; i < descriptors.Length; i++)
            payloadByteLength = checked(payloadByteLength + descriptors[i].Length);

        var descriptorByteLength = checked(_valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());
        return _nativeValues.Span.Slice(descriptorByteLength, payloadByteLength);
    }

    static ReadOnlySpan<T> ProjectBytes(ReadOnlySpan<byte> bytes)
    {
        if (typeof(T) != typeof(byte))
            throw new InvalidOperationException("Variable-length buffers must contain bytes.");
        if (bytes.IsEmpty)
            return [];
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(bytes)), bytes.Length);
    }

    void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)_valueCount)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}
