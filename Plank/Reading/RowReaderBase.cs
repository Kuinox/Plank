using Plank.Schema;

namespace Plank.Reading;

public abstract class RowReaderBase<TSlot> : IDisposable
    where TSlot : class
{
    bool _disposed;

    protected RowReaderBase(Stream stream, ParquetSchema schema, ParquetReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        Reader = schema.CreateReader(stream, options ?? ParquetReaderOptions.Default);
        _disposed = false;
    }

    protected ParquetReader Reader { get; }

    public ParquetFileMetadata Metadata
        => Reader.Metadata;

    public RowGroupTokenEnumerable EnumerateRowGroups()
        => Reader.EnumerateRowGroups();

    public void Dispose()
    {
        if (_disposed)
            return;

        Reader.Dispose();
        _disposed = true;
    }
}
