using Plank.Schema;

namespace Plank.Writing;

static class ByteStreamSplitEncoding
{
    internal static void WriteValue<T>(Column column, T value, ref BufferWriter writer)
        where T : notnull
        => PlainEncoding.WriteValue(column, value, ref writer);
}
