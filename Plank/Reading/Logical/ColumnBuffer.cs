using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Reading.Logical.Internal;

namespace Plank.Reading.Logical;

public readonly struct ColumnBuffer<T>
{
    readonly ParquetBuffer _nativeValues;
    readonly nint _variableLengthPayloadAddress;
    readonly int _valueCount;
    readonly bool _isVariableLength;

    internal ColumnBuffer(ParquetBuffer values, int valueCount)
    {
        _nativeValues = values;
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
        _variableLengthPayloadAddress = valueCount == 0
            ? 0
            : values.DangerousGetAddress() + checked(valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());
        _valueCount = valueCount;
        _isVariableLength = true;
    }

    public ReadOnlySpan<T> Values
        => _isVariableLength
            ? ProjectBytes(GetVariableLengthPayload())
            : ParquetBuffer.AsReadOnlySpan<T>(_nativeValues, _valueCount);

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
        return _nativeValues.RetainSlice(0, byteLength);
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
            return ParquetBuffer.AsSpan<T>(_nativeValues, _valueCount);
        }
    }

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
