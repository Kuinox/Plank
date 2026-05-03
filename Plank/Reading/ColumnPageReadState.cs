namespace Plank.Reading;

sealed class ColumnPageReadState<T>
{
    internal byte[]? Buffer;
    internal int BufferLength;
    internal Array? Dictionary;
    internal T[]? ValuesBuffer;
}
