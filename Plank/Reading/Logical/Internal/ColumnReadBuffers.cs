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

    internal Span<TValue> GetValues<TValue>(int valueCount, IParquetBufferPool bufferPool)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            throw new InvalidOperationException($"{typeof(TValue)} cannot be projected over unmanaged storage.");

        EnsureValues(checked(valueCount * Unsafe.SizeOf<TValue>()), bufferPool);
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<TValue>(Values, valueCount);
    }

    internal ColumnBuffer<T> CreateNativeBuffer(int valueCount)
        => new(Values, valueCount);

    internal Span<BinaryValueDescriptor> GetBinaryValues(int valueCount, int payloadByteLength,
        IParquetBufferPool bufferPool, out Span<byte> payload)
    {
        var descriptorByteLength = checked(valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());
        EnsureValues(checked(descriptorByteLength + payloadByteLength), bufferPool);
        payload = Values.Span.Slice(descriptorByteLength, payloadByteLength);
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<BinaryValueDescriptor>(Values, valueCount);
    }

    internal Span<TValue> GetDictionary<TValue>(int valueCount, IParquetBufferPool bufferPool)
    {
        Dictionary.Dispose();
        var byteLength = checked(valueCount * Unsafe.SizeOf<TValue>());
        Dictionary = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
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
        var descriptorByteLength = checked(valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());
        var byteLength = checked(descriptorByteLength + payloadByteLength);
        Dictionary = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
        DictionaryCount = valueCount;
        HasDictionary = true;
        payload = Dictionary.Span.Slice(descriptorByteLength, payloadByteLength);
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<BinaryValueDescriptor>(Dictionary, valueCount);
    }

    internal Span<byte> GetScratch(int byteLength, IParquetBufferPool bufferPool)
    {
        if (Scratch.Length < byteLength)
        {
            Scratch.Dispose();
            Scratch = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
        }

        return byteLength == 0 ? [] : Scratch.Span[..byteLength];
    }

    internal Span<byte> GetCompactDefinitions(int byteLength, IParquetBufferPool bufferPool)
    {
        if (CompactDefinitions.Length < byteLength)
        {
            CompactDefinitions.Dispose();
            CompactDefinitions = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
        }

        return byteLength == 0 ? [] : CompactDefinitions.Span[..byteLength];
    }

    internal ReadOnlySpan<byte> GetCompactDefinitions(int byteLength)
        => byteLength == 0 ? [] : CompactDefinitions.Span[..byteLength];

    internal Span<int> GetExpandedDefinitions(int valueCount, IParquetBufferPool bufferPool)
    {
        var byteLength = checked(valueCount * sizeof(int));
        if (ExpandedDefinitions.Length < byteLength)
        {
            ExpandedDefinitions.Dispose();
            ExpandedDefinitions = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
        }
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<int>(ExpandedDefinitions, valueCount);
    }

    internal void GetLevels(int levelCount, IParquetBufferPool bufferPool,
        out Span<int> repetitionLevels, out Span<int> definitionLevels)
    {
        Levels.Dispose();
        var byteLength = checked(levelCount * 2 * sizeof(int));
        Levels = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
        var levels = levelCount == 0 ? [] : ParquetBuffer.AsSpan<int>(Levels, checked(levelCount * 2));
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
        Values = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
    }
}
