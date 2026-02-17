using Plank.Schema;

namespace Plank.Writing;

static class DeltaByteArrayEncoding
{
    internal static void WriteValue<T>(Column column, T value, ref BufferWriter writer)
        where T : notnull
        => PlainEncoding.WriteValue(column, value, ref writer);
}
