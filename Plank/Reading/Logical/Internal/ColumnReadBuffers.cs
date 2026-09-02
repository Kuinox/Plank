using System.Runtime.CompilerServices;

namespace Plank.Reading.Logical.Internal;

struct ColumnReadBuffers<T>
{
    internal ParquetBuffer Values;
    internal ParquetBuffer Dictionary;
    internal ParquetBuffer Scratch;
    internal ParquetBuffer Levels;
    internal ParquetBuffer CompactDefinitions;
    internal ParquetBuffer ExpandedDefinitions;
    internal int DictionaryCount;
    internal bool HasDictionary;
    internal ReadOnlyMemory<byte> BorrowedBinaryDictionaryPayload;

    internal Span<TValue> GetValues<TValue>(int valueCount, IParquetBufferPool bufferPool)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            throw new InvalidOperationException($"{typeof(TValue)} cannot be projected over unmanaged storage.");

        EnsureValues(ByteLength(valueCount, Unsafe.SizeOf<TValue>(), "Value buffer"), bufferPool);
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<TValue>(Values, valueCount);
    }

    internal ColumnBuffer<T> CreateNativeBuffer(int valueCount)
        => new(Values, valueCount);

    internal ColumnBuffer<T> CreateBorrowedBinaryBuffer(int valueCount,
        ReadOnlyMemory<byte> payload, IParquetBufferPool bufferPool)
        => new(Values, valueCount, payload, bufferPool);

    internal Span<BinaryValueDescriptor> GetBinaryValues(int valueCount, int payloadByteLength,
        IParquetBufferPool bufferPool, out Span<byte> payload)
    {
        var descriptorByteLength = ByteLength(valueCount, Unsafe.SizeOf<BinaryValueDescriptor>(),
            "Binary value buffer");
        EnsureValues(Sum(descriptorByteLength, payloadByteLength, "Binary value buffer"), bufferPool);
        payload = Values.Span.Slice(descriptorByteLength, payloadByteLength);
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<BinaryValueDescriptor>(Values, valueCount);
    }

    internal Span<TValue> GetDictionary<TValue>(int valueCount, IParquetBufferPool bufferPool)
    {
        Dictionary.Dispose();
        BorrowedBinaryDictionaryPayload = default;
        var byteLength = ByteLength(valueCount, Unsafe.SizeOf<TValue>(), "Dictionary");
        Dictionary = byteLength == 0 ? default : bufferPool.Rent((uint)byteLength);
        DictionaryCount = valueCount;
        HasDictionary = true;
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<TValue>(Dictionary, valueCount);
    }

    internal ReadOnlySpan<TValue> GetDictionary<TValue>()
        => DictionaryCount == 0 ? [] : ParquetBuffer.AsReadOnlySpan<TValue>(Dictionary, DictionaryCount);

    internal Span<BinaryValueDescriptor> GetBinaryDictionary(int valueCount, int payloadByteLength,
        IParquetBufferPool bufferPool, out Span<byte> payload)
    {
        Dictionary.Dispose();
        BorrowedBinaryDictionaryPayload = default;
        var descriptorByteLength = ByteLength(valueCount, Unsafe.SizeOf<BinaryValueDescriptor>(),
            "Binary dictionary");
        var byteLength = Sum(descriptorByteLength, payloadByteLength, "Binary dictionary");
        Dictionary = byteLength == 0 ? default : bufferPool.Rent((uint)byteLength);
        DictionaryCount = valueCount;
        HasDictionary = true;
        payload = Dictionary.Span.Slice(descriptorByteLength, payloadByteLength);
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<BinaryValueDescriptor>(Dictionary, valueCount);
    }

    internal void SetBorrowedBinaryDictionaryPayload(ReadOnlyMemory<byte> payload)
        => BorrowedBinaryDictionaryPayload = payload;

    internal Span<byte> GetScratch(int byteLength, IParquetBufferPool bufferPool)
    {
        if (byteLength < 0)
            throw new CorruptParquetException($"Scratch buffer of {byteLength} bytes is not a valid size.");
        if (Scratch.Length < byteLength)
        {
            Scratch.Dispose();
            Scratch = byteLength == 0 ? default : bufferPool.Rent((uint)byteLength);
        }

        return byteLength == 0 ? [] : Scratch.Span[..byteLength];
    }

    internal Span<byte> GetCompactDefinitions(int byteLength, IParquetBufferPool bufferPool)
    {
        if (byteLength < 0)
            throw new CorruptParquetException(
                $"Definition level buffer of {byteLength} bytes is not a valid size.");
        if (CompactDefinitions.Length < byteLength)
        {
            CompactDefinitions.Dispose();
            CompactDefinitions = byteLength == 0 ? default : bufferPool.Rent((uint)byteLength);
        }

        return byteLength == 0 ? [] : CompactDefinitions.Span[..byteLength];
    }

    internal ReadOnlySpan<byte> GetCompactDefinitions(int byteLength)
        => byteLength == 0 ? [] : CompactDefinitions.Span[..byteLength];

    internal Span<int> GetExpandedDefinitions(int valueCount, IParquetBufferPool bufferPool)
    {
        var byteLength = ByteLength(valueCount, sizeof(int), "Definition level buffer");
        if (ExpandedDefinitions.Length < byteLength)
        {
            ExpandedDefinitions.Dispose();
            ExpandedDefinitions = byteLength == 0 ? default : bufferPool.Rent((uint)byteLength);
        }
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<int>(ExpandedDefinitions, valueCount);
    }

    internal void GetLevels(int levelCount, IParquetBufferPool bufferPool,
        out Span<int> repetitionLevels, out Span<int> definitionLevels)
    {
        Levels.Dispose();
        var byteLength = ByteLength(levelCount, 2 * sizeof(int), "Level buffer");
        Levels = byteLength == 0 ? default : bufferPool.Rent((uint)byteLength);
        var levels = levelCount == 0 ? [] : ParquetBuffer.AsSpan<int>(Levels, levelCount * 2);
        repetitionLevels = levels[..levelCount];
        definitionLevels = levels[levelCount..];
    }

    internal void Dispose()
    {
        Values.Dispose();
        Dictionary.Dispose();
        Scratch.Dispose();
        Levels.Dispose();
        CompactDefinitions.Dispose();
        ExpandedDefinitions.Dispose();
        this = default;
    }

    void EnsureValues(int byteLength, IParquetBufferPool bufferPool)
    {
        if (Values.Length >= byteLength && Values.IsExclusivelyOwned)
            return;
        Values.Dispose();
        Values = byteLength == 0 ? default : bufferPool.Rent((uint)byteLength);
    }

    // Every size in this type is derived from a page header, so a request that
    // does not fit in an int means the file is corrupt — not that Plank
    // miscalculated. checked() reported these as OverflowException, which is
    // not what callers of the reader are told to catch. Casting the product to
    // ulong also rejects a negative count in the same comparison.
    static int ByteLength(int count, int elementSize, string what)
    {
        var byteLength = (long)count * elementSize;
        if ((ulong)byteLength > int.MaxValue)
            throw new CorruptParquetException(
                $"{what} of {count} values requires more than {int.MaxValue} bytes.");
        return (int)byteLength;
    }

    static int Sum(int first, int second, string what)
    {
        var total = (long)first + second;
        if ((ulong)total > int.MaxValue)
            throw new CorruptParquetException(
                $"{what} requires more than {int.MaxValue} bytes.");
        return (int)total;
    }
}
