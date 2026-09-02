using System.Runtime.CompilerServices;

namespace Plank.Reading.Logical.Internal;

readonly unsafe struct BinaryValueDescriptor
{
    readonly int _offsetPlusOne;
    readonly int _length;

    internal BinaryValueDescriptor(int offset, int length)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        _offsetPlusOne = checked(offset + 1);
        _length = length;
    }

    internal bool IsNull
        => _offsetPlusOne == 0;

    internal int Length
        => _length;

    internal int Offset
        => IsNull ? 0 : _offsetPlusOne - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> GetSpan(nint payloadAddress)
        => IsNull ? [] : new ReadOnlySpan<byte>((void*)(payloadAddress + _offsetPlusOne - 1), _length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> GetSpan(byte[] payload, int payloadOffset)
        => IsNull ? [] : new ReadOnlySpan<byte>(payload,
            checked(payloadOffset + _offsetPlusOne - 1), _length);
}
