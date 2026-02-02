namespace Plank;

public sealed class ParquetWriter : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ParquetSchema _schema;
    private readonly ParquetWriterOptions _options;

    private ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default);
    }

    public RowGroupWriter StartRowGroup(int rowCount, RowGroupOptions? options = null)
    {
        if (rowCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be non-negative.");
        }

        return new RowGroupWriter(this, rowCount, options ?? RowGroupOptions.Default);
    }

    internal Stream Stream => _stream;

    internal ParquetSchema Schema => _schema;

    internal ParquetWriterOptions Options => _options;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
