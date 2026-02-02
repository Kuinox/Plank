namespace Plank;

public sealed class ParquetSchemaNotGeneratedException : InvalidOperationException
{
    public ParquetSchemaNotGeneratedException(Type type)
        : base($"No generated Parquet schema was registered for {type}.")
    {
        Type = type;
    }

    public Type Type { get; }
}
