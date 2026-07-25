using Plank.Writing.PageStrategy;

namespace Plank.Schema;

/// <summary>Identifies a flattened leaf column owned by a <see cref="ParquetSchema"/>.</summary>
/// <remarks>
/// Leaf columns are derived from the schema definition tree and are used to select columns for column-oriented reads
/// and writes. Obtain them from <see cref="ParquetSchema.LeafColumns"/>.
/// </remarks>
public sealed class LeafColumn
{
    internal readonly Column Column;

    internal LeafColumn(Column column, int ordinal)
    {
        Column = column;
        Ordinal = ordinal;
    }

    /// <summary>Gets the leaf's ordinal in the flattened schema.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the complete dot-separated path to the leaf.</summary>
    public string Path
        => Column.Name;

    /// <summary>Gets the leaf's physical Parquet type.</summary>
    public ParquetPhysicalType PhysicalType
        => Column.PhysicalType;

    /// <summary>Gets the leaf's encoding and physical storage options.</summary>
    public ColumnOptions Options
        => Column.Options;

    /// <summary>Gets the leaf's logical type, if one is declared.</summary>
    public LogicalType? LogicalType
        => Column.LogicalType;

    /// <summary>Gets the writer page strategy declared for this leaf, if any.</summary>
    public IPageStrategy? PageStrategy
        => Column.PageStrategy;
}
