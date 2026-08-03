using Plank.Schema;
using Plank.Reading.Logical;

namespace Plank.Writing;

public sealed class ParquetFileMerger
{
    readonly ParquetWriter _writer;
    readonly ParquetReader _reader;
    readonly bool _preserveFirstFileMetadata;
    bool _closed;

    internal ParquetFileMerger(Stream destination, ParquetSchema schema, ParquetMergeOptions options)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("Merging requires a writable, seekable destination stream.",
                nameof(destination));
        if (destination.Position != 0 || destination.Length != 0)
            throw new ArgumentException("The merge destination stream must be empty and positioned at zero.",
                nameof(destination));

        options.Validate();
        _reader = new ParquetReader(schema, new ParquetReaderOptions
        {
            BufferPool = options.WriterOptions.BufferPool,
            Strict = true
        });
        try
        {
            _writer = new ParquetWriter(destination, schema, options.WriterOptions);
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
        _preserveFirstFileMetadata = options.PreserveFirstFileMetadata;
    }

    public int SourceFileCount { get; private set; }

    public int RowGroupCount { get; private set; }

    public long RowCount { get; private set; }

    public void AppendFile(Stream source)
    {
        if (_closed)
            throw new InvalidOperationException("The merged file is already closed.");

        var (rowGroupCount, rowCount) = _writer.ImportFile(source, _reader,
            preserveMetadata: SourceFileCount == 0 && _preserveFirstFileMetadata);
        SourceFileCount = checked(SourceFileCount + 1);
        RowGroupCount = checked(RowGroupCount + rowGroupCount);
        RowCount = checked(RowCount + rowCount);
    }

    public void CloseFile()
    {
        if (_closed)
            throw new InvalidOperationException("The merged file is already closed.");

        _writer.CloseFile();
        _reader.Dispose();
        _closed = true;
    }
}
