namespace Plank.Reading;

using System.Runtime.CompilerServices;
using Plank.Writing;

sealed class ColumnPageReadState<T>
{
    internal byte[]? Buffer;
    internal int BufferLength;
    internal Array? Dictionary;
    internal T[]? DictionaryBuffer;
    internal T[]? ValuesBuffer;

    internal void Release(IParquetBufferPool bufferPool)
    {
        ArgumentNullException.ThrowIfNull(bufferPool);

        if (Buffer is not null)
        {
            bufferPool.Return(Buffer);
            Buffer = null;
            BufferLength = 0;
        }

        if (ValuesBuffer is not null)
        {
            ArrayRenter<T>.Shared.Return(ValuesBuffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            ValuesBuffer = null;
        }

        if (DictionaryBuffer is not null)
        {
            ArrayRenter<T>.Shared.Return(DictionaryBuffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            DictionaryBuffer = null;
        }

        Dictionary = null;
    }
}
