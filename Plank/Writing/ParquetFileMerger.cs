using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Writing;

public sealed class ParquetFileMerger
{
    readonly ParquetWriter _writer;
    readonly ParquetReader _reader;
    readonly bool _preserveFirstFileMetadata;
    bool _closed;

    internal ParquetFileMerger(IParquetReadSource source, IParquetWriteSource destination, ParquetSchema schema,
        ParquetMergeOptions options)
        : this(ValidateDistinctDestination(source, destination), schema, options, existingSource: null)
    {
        try
        {
            AppendFile(source);
        }
        catch
        {
            AbortConstruction();
            throw;
        }
    }

    internal ParquetFileMerger(IParquetReadWriteSource destination, ParquetSchema schema,
        ParquetMergeOptions options)
        : this(destination, schema, options, destination)
    {
    }

    ParquetFileMerger(IParquetWriteSource destination, ParquetSchema schema, ParquetMergeOptions options,
        IParquetReadSource? existingSource)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _reader = new ParquetReader(schema, new ParquetReaderOptions
        {
            BufferPool = options.WriterOptions.BufferPool,
            Strict = true
        });
        try
        {
            if (existingSource is null)
            {
                destination.SetLength(0);
                _writer = new ParquetWriter(destination, schema, options.WriterOptions);
            }
            else
            {
                InitializeCounts(existingSource);
                _writer = new ParquetWriter(existingSource, destination, schema, new ParquetAppendOptions
                {
                    WriterOptions = options.WriterOptions,
                    PreserveExistingMetadata = options.PreserveFirstFileMetadata
                });
            }
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
        _preserveFirstFileMetadata = existingSource is null && options.PreserveFirstFileMetadata;
    }

    public int SourceFileCount { get; private set; }

    public int RowGroupCount { get; private set; }

    public long RowCount { get; private set; }

    public void AppendFile(IParquetReadSource source)
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

    void InitializeCounts(IParquetReadSource source)
    {
        _reader.Reset(source);
        var metadata = _reader.PhysicalReader.Metadata;
        long rowCount = 0;
        for (var i = 0; i < metadata.RowGroupCount; i++)
            rowCount = checked(rowCount + checked((long)metadata.RowGroups[i].RowCount));

        SourceFileCount = 1;
        RowGroupCount = metadata.RowGroupCount;
        RowCount = rowCount;
    }

    void AbortConstruction()
    {
        try
        {
            _writer.CloseFile();
        }
        catch
        {
        }
        _reader.Dispose();
        _closed = true;
    }

    static IParquetWriteSource ValidateDistinctDestination(IParquetReadSource source,
        IParquetWriteSource destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(source, destination))
            throw new ArgumentException("The source and destination must be different for an out-of-place merge.",
                nameof(destination));
        return destination;
    }
}
