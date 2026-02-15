using Plank.Schema;

namespace Plank2.Writing;

static class Encoding
{
    internal static void Encode<T>(Column column, ReadOnlySpan<T> values, IPageStrategy strategy,
        PageList pages)
        => TODO;
}
