using Plank.Schema;

namespace Plank.Writing;

readonly struct ResolvedCompression
{
    internal ResolvedCompression(CompressionKind kind, int level)
    {
        Kind = kind;
        Level = level;
    }

    internal readonly CompressionKind Kind;

    internal readonly int Level;
}
