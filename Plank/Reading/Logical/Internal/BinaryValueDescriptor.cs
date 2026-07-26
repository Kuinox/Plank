namespace Plank.Reading.Logical.Internal;

readonly unsafe struct BinaryValueDescriptor
{
    readonly nint _data;
    readonly int _length;

    internal BinaryValueDescriptor(nint data, int length)
    {
        _data = data;
        _length = length;
    }

    internal bool IsNull
        => _data == 0;

    internal int Length
        => _length;

    internal ReadOnlySpan<byte> Span
        => _data == 0 ? [] : new ReadOnlySpan<byte>((void*)_data, _length);
}
