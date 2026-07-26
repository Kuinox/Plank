using System.Runtime.CompilerServices;

namespace Plank.Reading.Logical.Internal;

struct ColumnReadBuffers<T>
{
    internal ParquetBuffer Values;
    internal ParquetBuffer Dictionary;
    internal ParquetBuffer Scratch;
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

    internal void Dispose()
    {
        Values.Dispose();
        Dictionary.Dispose();
        Scratch.Dispose();
        this = default;
    }

    void EnsureValues(int byteLength, IParquetBufferPool bufferPool)
    {
        Values.Dispose();
        Values = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
    }
}
