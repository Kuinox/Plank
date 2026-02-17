using System.Buffers.Binary;

namespace Plank2.Writing;

static class PlainDictionaryEncoding
{
    internal static void WriteIndex(int index, ref BufferWriter writer)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, index);
        writer.Advance(sizeof(int));
    }
}
