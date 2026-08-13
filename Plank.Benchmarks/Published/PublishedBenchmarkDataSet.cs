namespace Plank.Benchmarks.Published;

public sealed class PublishedBenchmarkDataSet
{
    public required string SuiteId { get; init; }

    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Encoding { get; init; }

    public required string ThroughputUnit { get; init; }

    public required IReadOnlyList<Column> Columns { get; init; }

    public IReadOnlyList<string> DataTypes
        => Columns.Select(static column => DisplayName(column.Kind)).Distinct().ToArray();

    public int RowGroupCount
        => Columns.Count == 0 ? 0 : Columns[0].Values.Count;

    public long RowCount
        => Columns.Count == 0 ? 0 : Columns[0].Values.Sum(static values => values.Length);

    public long ValueCount
        => checked(RowCount * Columns.Count);

    static string DisplayName(BenchmarkColumnKind kind)
        => kind switch
        {
            BenchmarkColumnKind.Boolean => "boolean",
            BenchmarkColumnKind.Int32 => "int32",
            BenchmarkColumnKind.Int64 => "int64",
            BenchmarkColumnKind.Timestamp => "timestamp",
            BenchmarkColumnKind.Double => "double",
            BenchmarkColumnKind.String => "string",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    public sealed class Column
    {
        public required string Name { get; init; }

        public required BenchmarkColumnKind Kind { get; init; }

        public required bool Nullable { get; init; }

        public required IReadOnlyList<Array> Values { get; init; }

        public IReadOnlyList<Array>? Utf8Values { get; init; }
    }
}
