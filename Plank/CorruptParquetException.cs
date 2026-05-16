namespace Plank;

public sealed class CorruptParquetException : Exception
{
    public CorruptParquetException(string message) : base(message)
    {
    }

    public CorruptParquetException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
