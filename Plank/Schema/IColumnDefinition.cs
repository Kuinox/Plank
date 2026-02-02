namespace Plank;

internal interface IColumnDefinition
{
    ParquetSchema.Column Create(int ordinal);
}
