namespace Plank.RowApi;

/// <summary>Provides a generated binary property's current zero-copy value and ownership operation.</summary>
/// <remarks>
/// The value is a temporary view over the row reader's current buffers. Call <see cref="Retain"/>
/// before advancing the reader when the bytes must remain available independently.
/// </remarks>
public readonly ref struct RowReaderBinaryValue
{
    readonly RowApiBinaryColumnReadState? _state;
    readonly int _index;

    internal RowReaderBinaryValue(RowApiBinaryColumnReadState state, int index, bool isNull)
    {
        _state = state;
        _index = index;
        IsNull = isNull;
    }

    /// <summary>Gets the current byte value.</summary>
    public ReadOnlySpan<byte> Value
        => _state is null || _index < 0 || IsNull ? [] : _state.GetValue(_index);

    /// <summary>Gets the current byte value.</summary>
    public ReadOnlySpan<byte> Span
        => Value;

    /// <summary>Gets the number of bytes in the current value.</summary>
    public int Length
        => Value.Length;

    /// <summary>Gets whether the current value contains no bytes.</summary>
    public bool IsEmpty
        => Value.IsEmpty;

    /// <summary>Gets whether the current value is null.</summary>
    public bool IsNull { get; }

    /// <summary>Retains the current value independently of the row reader's position.</summary>
    /// <returns>
    /// A reference-counted buffer containing the current value, or an empty buffer when the value
    /// is null or empty. Dispose the returned buffer when it is no longer needed.
    /// </returns>
    public ParquetBuffer Retain()
        => _state is null || _index < 0 || IsNull ? default : _state.RetainValue(_index);
}
