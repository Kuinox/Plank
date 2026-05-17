namespace Plank.Reading;

using Plank.Writing;

interface IColumnPageReadState
{
    void ReleaseAll(IParquetBufferPool bufferPool);
}
