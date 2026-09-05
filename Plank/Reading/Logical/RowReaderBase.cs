using Plank.Schema;

namespace Plank.Reading.Logical;

abstract class RowReaderBase<TSlot> : IDisposable
    where TSlot : class
{
    bool _disposed;

    protected RowReaderBase(Stream stream, ParquetSchema schema, ParquetReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        Reader = schema.CreateReader(stream, options);
        _disposed = false;
    }

    protected ParquetReader Reader { get; }

    public ParquetFileMetadata Metadata
        => Reader.Metadata;

    public RowGroupCollection RowGroups
        => Reader.RowGroups;

    public void Dispose()
    {
        if (_disposed)
            return;

        Reader.Dispose();
        _disposed = true;
    }
}
