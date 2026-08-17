namespace Plank.Writing.Encoding;

/// <summary>
/// The smallest and largest value a plain BYTE_ARRAY encode pass saw, as indices into the value span
/// it was handed.
/// </summary>
/// <remarks>
/// The page writer already touches every value to copy it, so it can decide the column's min and max
/// on the way past instead of leaving a later statistics pass to walk the same heap references again.
/// Indices rather than the values themselves keep this a plain struct that both row shapes
/// (<see cref="byte"/>[] and <see cref="ReadOnlyMemory{T}"/>) can fill.
/// <para>
/// Ordering is plain unsigned lexicographic, which is the sort order for every BYTE_ARRAY column
/// except <see cref="Plank.Schema.LogicalType.Decimal"/>. The consumer is responsible for ignoring
/// this when the column orders its bytes differently.
/// </para>
/// </remarks>
struct PlainBinaryMinMax
{
    /// <summary>Whether a value was seen at all. An empty column leaves this false.</summary>
    internal bool Found;

    internal int MinIndex;

    internal int MaxIndex;
}
