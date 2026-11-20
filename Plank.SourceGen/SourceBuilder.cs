using System.Text;

namespace Plank.SourceGen;

static class SourceBuilder
{
    [ThreadStatic]
    static StringBuilder? cached;

    internal static StringBuilder Rent()
    {
        var builder = cached;
        cached = null;
        return builder ?? new StringBuilder(16 * 1024);
    }

    internal static string Finish(StringBuilder builder)
    {
        var source = builder.ToString();
        // Keep one buffer for the next schema on this compiler thread. Release unusually large ones.
        if (builder.Capacity <= 32 * 1024)
            cached = builder.Clear();
        return source;
    }
}
