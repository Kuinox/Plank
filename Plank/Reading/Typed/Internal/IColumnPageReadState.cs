namespace Plank.Reading.Typed.Internal;

using Plank.Writing;

interface IColumnPageReadState
{
    void ReleaseAll(IParquetBufferPool bufferPool);
}
