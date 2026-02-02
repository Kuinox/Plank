using Plank.Schema;

namespace Plank.Writing;

public sealed class ParquetWriter : IDisposable, IAsyncDisposable
{
    readonly Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default);
    }

    public RowGroupWriter StartRowGroup(int rowCount, RowGroupOptions? options = null)
    {
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be non-negative.");

        return new RowGroupWriter(this, rowCount, options ?? RowGroupOptions.Default);
    }

    internal Stream Stream => _stream;

    internal ParquetSchema Schema => _schema;

    internal ParquetWriterOptions Options => _options;

    public void Dispose()
        => _stream.Dispose();

    public ValueTask DisposeAsync()
        => _stream.DisposeAsync();
}
