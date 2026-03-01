using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record RowSchema
{
    public RowSchema(ImmutableArray<RowSchemaColumn> columns)
    {
        Columns = columns.IsDefault ? [] : columns;
        ParquetSchema = new ParquetSchema(Columns.Select(static c => c.ToColumn()).ToImmutableArray());
    }

    public ImmutableArray<RowSchemaColumn> Columns { get; }

    public ParquetSchema ParquetSchema { get; }

    public static RowSchema Create(params RowSchemaColumn[] columns)
        => new((columns ?? throw new ArgumentNullException(nameof(columns))).ToImmutableArray());

    public static RowSchema Create(ImmutableArray<RowSchemaColumn> columns)
        => new(columns);

    public static RowSchemaColumn Column<TClr>(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null,
        LogicalType? logicalType = null)
        => new(name, physicalType, typeof(TClr), options, logicalType);

}
