namespace Plank.Schema;

public sealed record ColumnDefinition(string Name, Type ClrType, ColumnOptions Options)
{
    public static ColumnDefinition Create<T>(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new ColumnDefinition(name, typeof(T), ColumnOptions.Default);
    }
}
