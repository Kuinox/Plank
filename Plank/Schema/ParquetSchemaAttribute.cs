namespace Plank.Schema;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ParquetSchemaAttribute : Attribute
{
    public bool AllowAllocatingValues { get; set; }
}
