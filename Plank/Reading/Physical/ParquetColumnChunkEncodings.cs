using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Reading.Physical;

// Collection of encodings for a column chunk, limited to 10 values.
public readonly struct ParquetColumnChunkEncodings
{
    internal const int MaxCount = 10;
    readonly EncodingBuffer _buffer;

    internal ParquetColumnChunkEncodings(EncodingBuffer buffer, int count)
    {
        _buffer = buffer;
        Count = count;
    }

    public int Count { get; }

    public EncodingKind this[int ordinal]
    {
        get
        {
            if ((uint)ordinal >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal,
                    $"Ordinal must be between zero and {Count - 1}.");

            return _buffer[ordinal];
        }
    }

    internal void CopyTo(Span<EncodingKind> destination)
    {
        for (var i = 0; i < Count; i++)
            destination[i] = _buffer[i];
    }

    [InlineArray(MaxCount)]
    internal struct EncodingBuffer
    {
        EncodingKind _element0;
    }
}
