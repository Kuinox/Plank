namespace Plank.Reading.Logical.Internal;

using System.Runtime.CompilerServices;
using Plank.Writing;

sealed class ColumnPageReadState<T> : IColumnPageReadState
{
    internal Array? Dictionary;
    internal T[]? DictionaryBuffer;
    internal T[]? ValuesBuffer;
    internal int[]? DeltaPrefixLengthsBuffer;
    internal int[]? DeltaSuffixLengthsBuffer;

    public void ReleaseAll(IParquetBufferPool bufferPool)
    {
        ArgumentNullException.ThrowIfNull(bufferPool);

        if (ValuesBuffer is not null)
        {
            bufferPool.Return(ValuesBuffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            ValuesBuffer = null;
        }

        if (DictionaryBuffer is not null)
        {
            bufferPool.Return(DictionaryBuffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            DictionaryBuffer = null;
        }

        if (DeltaPrefixLengthsBuffer is not null)
        {
            bufferPool.Return(DeltaPrefixLengthsBuffer);
            DeltaPrefixLengthsBuffer = null;
        }

        if (DeltaSuffixLengthsBuffer is not null)
        {
            bufferPool.Return(DeltaSuffixLengthsBuffer);
            DeltaSuffixLengthsBuffer = null;
        }

        Dictionary = null;
    }
}
