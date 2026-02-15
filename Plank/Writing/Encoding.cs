using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Writing;

static partial class Encoding
{
    internal static EncodingKind ResolveDefault(ImmutableArray<EncodingKind> encodings)
        => encodings.IsDefaultOrEmpty ? EncodingKind.Plain : encodings[0];
}
