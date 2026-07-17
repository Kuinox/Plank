
using System.Runtime.CompilerServices;
using Plank.Writing;

namespace Plank.Reading.Logical.Internal;

struct ColumnReadBuffers<T>
{
    internal Array? ManagedDictionary;
    internal T[]? ManagedDictionaryBuffer;
    internal T[]? ManagedValuesBuffer;
    internal ParquetBuffer Values;
    internal ParquetBuffer Dictionary;
    internal ParquetBuffer Scratch;
    internal int DictionaryCount;
    internal bool HasDictionary;

    internal ColumnBuffer<T> CreateBuffer(ReadOnlyMemory<T> values, IParquetBufferPool bufferPool)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return new ColumnBuffer<T>(values);

        var byteLength = checked(values.Length * Unsafe.SizeOf<T>());
        EnsureValues(byteLength, bufferPool);
        if (byteLength > 0)
            values.Span.CopyTo(ParquetBuffer.AsSpan<T>(Values, values.Length));
        return new ColumnBuffer<T>(Values, values.Length);
    }

    internal Span<TValue> GetValues<TValue>(int valueCount, IParquetBufferPool bufferPool)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            throw new InvalidOperationException($"{typeof(TValue)} cannot be projected over unmanaged storage.");

        EnsureValues(checked(valueCount * Unsafe.SizeOf<TValue>()), bufferPool);
        return valueCount == 0 ? [] : ParquetBuffer.AsSpan<TValue>(Values, valueCount);
    }

    internal ColumnBuffer<T> CreateNativeBuffer(int valueCount)
        => new(Values, valueCount);

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

    internal Span<byte> GetScratch(int byteLength, IParquetBufferPool bufferPool)
    {
        if (Scratch.Length < byteLength)
        {
            Scratch.Dispose();
            Scratch = byteLength == 0 ? default : bufferPool.Rent(checked((uint)byteLength));
        }

        return byteLength == 0 ? [] : Scratch.Span[..byteLength];
    }

    internal void Dispose(IParquetBufferPool bufferPool)
    {
        if (ManagedValuesBuffer is not null)
            ArrayRenter<T>.Shared.Return(ManagedValuesBuffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        if (ManagedDictionaryBuffer is not null && !ReferenceEquals(ManagedDictionaryBuffer, ManagedValuesBuffer))
            ArrayRenter<T>.Shared.Return(ManagedDictionaryBuffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
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
