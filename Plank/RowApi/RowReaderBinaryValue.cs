using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.RowApi;

/// <summary>Provides a generated binary property's current zero-copy value and ownership operation.</summary>
/// <remarks>
/// The value is a temporary view over the row reader's current buffers. Call <see cref="Retain"/>
/// before advancing the reader when the bytes must remain available independently.
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

    /// <summary>Gets the current byte value.</summary>
    public ReadOnlySpan<byte> Span
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

    /// <summary>Retains the current value independently of the row reader's position.</summary>
    /// <returns>
    /// A reference-counted buffer containing the current value, or an empty buffer when the value
    /// is null or empty. Dispose the returned buffer when it is no longer needed.
    /// </returns>
    public ParquetBuffer Retain()
    {
        if (IsNull || _value.IsEmpty)
            return default;

        using var rented = DefaultParquetBufferPool.Shared.Rent(checked((uint)_value.Length));
        _value.CopyTo(rented.Span);
        return rented.RetainSlice(0, _value.Length);
    }
}
