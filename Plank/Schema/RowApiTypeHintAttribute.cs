namespace Plank.Schema;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class RowApiTypeHintAttribute : Attribute
{
    public RowApiTypeHintAttribute(string columnName, Type clrType)
    {
        ColumnName = columnName;
        ClrType = clrType;
    }

    public string ColumnName { get; }

    public Type ClrType { get; }
}
