using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record RowSchema<T>(ImmutableArray<RowColumnDefinition<T>> Columns);
