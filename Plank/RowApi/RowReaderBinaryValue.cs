using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.RowApi;

/// <summary>Provides a generated binary property's current zero-copy value.</summary>
/// <remarks>
/// The value is a temporary view over the row reader's current buffers and must be consumed before
/// advancing the reader. Copy the span into caller-owned storage when the bytes must remain available.
/// </remarks>
public readonly ref struct RowReaderBinaryValue
{
    static readonly byte[] s_nonNullEmpty = new byte[1];

    readonly ReadOnlySpan<byte> _value;

    internal RowReaderBinaryValue(ReadOnlySpan<byte> value)
        => _value = value;

    // A default span represents null. Anchor non-null empty values to a real array so
    // nullness does not require another field in this hot-path value.
    internal static ReadOnlySpan<byte> NonNullEmpty
        => new ReadOnlySpan<byte>(s_nonNullEmpty, 0, 0);

    /// <summary>Gets the current byte value.</summary>
    public ReadOnlySpan<byte> Value
        => _value;

    /// <summary>Gets the number of bytes in the current value.</summary>
    public int Length
        => _value.Length;

    /// <summary>Gets whether the current value contains no bytes.</summary>
    public bool IsEmpty
        => _value.IsEmpty;

    /// <summary>Gets whether the current value is null.</summary>
    public bool IsNull
        => Unsafe.IsNullRef(ref MemoryMarshal.GetReference(_value));

}
