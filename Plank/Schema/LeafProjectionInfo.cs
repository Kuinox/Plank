using System.Collections.Immutable;

namespace Plank.Schema;

internal readonly record struct LeafProjectionInfo(bool IsList, bool ListOptional, bool ElementOptional,
    int MaxRepetitionLevel, int MaxDefinitionLevel, ImmutableArray<MapProjectionInfo> MapProjections = default);
