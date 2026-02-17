using Plank.Schema;

namespace Plank2.Writing;

static class DeltaBinaryPackedEncoding
{
    internal static void WriteValue<T>(Column column, T value, ref BufferWriter writer)
        where T : notnull
        => PlainEncoding.WriteValue(column, value, ref writer);
}
