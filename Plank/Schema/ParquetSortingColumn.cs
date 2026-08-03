namespace Plank.Schema;

/// <summary>Describes one leaf in a row group's lexicographic sort order.</summary>
public readonly struct ParquetSortingColumn
{
    /// <summary>Creates a sorting declaration for a leaf column.</summary>
    /// <param name="columnOrdinal">The leaf column ordinal in the flattened file schema.</param>
    /// <param name="descending"><see langword="true"/> when values are sorted descending.</param>
    /// <param name="nullsFirst"><see langword="true"/> when nulls precede non-null values.</param>
    public ParquetSortingColumn(int columnOrdinal, bool descending = false, bool nullsFirst = false)
    {
        ColumnOrdinal = columnOrdinal;
        Descending = descending;
        NullsFirst = nullsFirst;
    }

    public int ColumnOrdinal { get; }

    public bool Descending { get; }

    public bool NullsFirst { get; }
}
