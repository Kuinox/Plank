namespace Plank.Reading.Logical.Internal;

using Plank.Writing;

interface IColumnPageReadState
{
    void ReleaseAll(IParquetBufferPool bufferPool);
}
