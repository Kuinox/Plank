namespace Plank.Tests;

sealed class ParquetFileReadResult
{
    public required IReadOnlyList<ParquetRowGroupReadResult> RowGroups { get; init; }
}
