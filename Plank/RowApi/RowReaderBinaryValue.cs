using Plank.Reading.Logical;

namespace Plank.RowApi;

/// <summary>Provides a generated binary property's current zero-copy value and null state.</summary>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public readonly ref struct RowReaderBinaryValue
{
    readonly ColumnBuffer<byte> _buffer;
    readonly int _index;
    readonly bool _hasValue;

    internal RowReaderBinaryValue(ColumnBuffer<byte> buffer, int index, bool isNull)
    {
        _buffer = buffer;
        _index = index;
        _hasValue = index >= 0;
        IsNull = isNull;
    }

    /// <summary>Gets the current byte value.</summary>
    public ReadOnlySpan<byte> Value
        => _hasValue ? _buffer.GetValue(_index) : [];

    /// <summary>Gets whether the current value is null.</summary>
    public bool IsNull { get; }

    /// <summary>Retains the current byte value so it can outlive the reader's current row.</summary>
    /// <returns>A reference-counted buffer containing the current value, or an empty buffer for a null value.</returns>
    /// <remarks>Call this before advancing the reader and dispose the returned buffer when it is no longer needed.</remarks>
    public ParquetBuffer Retain()
        => _hasValue ? _buffer.RetainValue(_index) : default;
}
