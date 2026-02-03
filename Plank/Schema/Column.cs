namespace Plank.Schema;

public sealed record Column(string Name, Type ClrType, ColumnOptions Options);
