using System.Collections.Immutable;
using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record RowSchema
{
    public RowSchema(ImmutableArray<RowSchemaColumn> columns)
    {
        Columns = columns.IsDefault ? [] : columns;
        ParquetSchema = new ParquetSchema(Columns.Select(static c => c.ToDefinition()).ToImmutableArray());
    }

    public ImmutableArray<RowSchemaColumn> Columns { get; }

    public ParquetSchema ParquetSchema { get; }

    public static RowSchema Create(params RowSchemaColumn[] columns)
        => new((columns ?? throw new ArgumentNullException(nameof(columns))).ToImmutableArray());

    public static RowSchema Create(ImmutableArray<RowSchemaColumn> columns)
        => new(columns);

    public static RowSchemaColumn Column<TClr>(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null,
        LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => new(name, physicalType, typeof(TClr), options, logicalType, pageStrategy);

    public static RowSchemaColumn Column<TClr>(string name, ParquetPhysicalType physicalType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        => new(name, physicalType, typeof(TClr), fieldId, options, logicalType, pageStrategy);

}
