namespace Plank.RowApi;

/// <summary>Provides a generated binary property's current zero-copy value and null state.</summary>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public readonly ref struct RowReaderBinaryValue
{
    readonly ReadOnlySpan<byte> _value;

    internal RowReaderBinaryValue(ReadOnlySpan<byte> value, bool isNull)
    {
        _value = value;
        IsNull = isNull;
    }

    /// <summary>Gets the current byte value.</summary>
    public ReadOnlySpan<byte> Value
        => _value;

    /// <summary>Gets whether the current value is null.</summary>
    public bool IsNull { get; }
}
