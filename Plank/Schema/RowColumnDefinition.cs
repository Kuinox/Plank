using System.Linq.Expressions;

namespace Plank.Schema;

public sealed record RowColumnDefinition<T>(LambdaExpression Expression, ParquetPhysicalType PhysicalType, ColumnOptions Options)
{
    public string? Name { get; init; }
}
