namespace Plank.Tests.E2E.Interop;

sealed class ParquetFileReadResult
{
    public required IReadOnlyList<ParquetRowGroupReadResult> RowGroups { get; init; }
}
