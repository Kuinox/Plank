namespace Plank.Schema;

public sealed record ColumnDefinition(string Name, Type ClrType, ColumnOptions Options);
