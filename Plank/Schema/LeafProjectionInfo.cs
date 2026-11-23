using System.Collections.Immutable;

namespace Plank.Schema;

/// <param name="RepeatedDefinitionLevels">The definition level of each repeated node on the leaf's path, outermost
/// first. Optional groups around a list or inside its element add levels of their own, so these cannot be derived
/// from the list and element optionality alone.</param>
internal readonly record struct LeafProjectionInfo(bool IsList, bool ListOptional, bool ElementOptional,
    int MaxRepetitionLevel, int MaxDefinitionLevel, ImmutableArray<MapProjectionInfo> MapProjections = default,
    ImmutableArray<int> RepeatedDefinitionLevels = default);
