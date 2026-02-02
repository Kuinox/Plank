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

    public RowGroupWriter StartRowGroup(RowGroupOptions? options = null)
        => new RowGroupWriter(this, options ?? RowGroupOptions.Default);

    internal Stream Stream => _stream;

    internal ParquetSchema Schema => _schema;

    internal ParquetWriterOptions Options => _options;

    public void Dispose()
        => _stream.Dispose();

    public ValueTask DisposeAsync()
        => _stream.DisposeAsync();
}
