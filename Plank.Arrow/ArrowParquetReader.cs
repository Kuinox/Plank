using Apache.Arrow;
using Plank.Reading.Logical;

namespace Plank.Arrow;

/// <summary>Materializes Plank row groups as Apache Arrow record batches or a table.</summary>
public sealed class ArrowParquetReader : IDisposable
{
    readonly ParquetReader _reader;
    bool _disposed;

    /// <summary>Opens a seekable Parquet stream and derives its supported flat Arrow schema.</summary>
    /// <remarks>The source stream remains owned by the caller and is not closed by this reader.</remarks>
    public ArrowParquetReader(Stream source, ParquetReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _reader = new ParquetReader(options);
        try
        {
            _reader.Reset(source);
            Schema = ArrowSchemaConverter.ToArrowSchema(_reader.Schema);
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    /// <summary>Gets the flattened Arrow schema derived from the Parquet file.</summary>
    public Apache.Arrow.Schema Schema { get; }

    /// <summary>Gets the number of Parquet row groups available as record batches.</summary>
    public int RecordBatchCount
    {
        get
        {
            ThrowIfDisposed();
            return _reader.RowGroups.Count;
        }
    }

    /// <summary>Reads one Parquet row group into an independently owned Arrow record batch.</summary>
    public RecordBatch ReadRecordBatch(int index)
    {
        ThrowIfDisposed();
        var rowGroup = _reader.RowGroups[index];
        return ArrowColumnReader.ReadRecordBatch(rowGroup, Schema, _reader.Schema.LeafColumns);
    }

    /// <summary>Reads every Parquet row group into independently owned Arrow record batches.</summary>
    /// <remarks>The caller owns and should dispose every returned record batch.</remarks>
    public IReadOnlyList<RecordBatch> ReadRecordBatches()
    {
        ThrowIfDisposed();
        return ReadAllRecordBatches();
    }

    /// <summary>Reads every Parquet row group into one Arrow table whose columns retain row-group chunking.</summary>
    public Table ReadTable()
    {
        ThrowIfDisposed();
        var batches = ReadAllRecordBatches();
        if (batches.Length != 0)
            return Table.TableFromRecordBatches(Schema, batches);

        var arrays = new IArrowArray[Schema.FieldsList.Count];
        for (var i = 0; i < arrays.Length; i++)
            arrays[i] = ArrowColumnReader.CreateEmpty(Schema.GetFieldByIndex(i));
        var emptyBatch = new RecordBatch(Schema, arrays, 0);
        return Table.TableFromRecordBatches(Schema, [emptyBatch]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _reader.Dispose();
    }

    RecordBatch[] ReadAllRecordBatches()
    {
        var batches = new RecordBatch[_reader.RowGroups.Count];
        var count = 0;
        try
        {
            for (; count < batches.Length; count++)
                batches[count] = ArrowColumnReader.ReadRecordBatch(_reader.RowGroups[count], Schema,
                    _reader.Schema.LeafColumns);
            return batches;
        }
        catch
        {
            for (var i = 0; i < count; i++)
                batches[i].Dispose();
            throw;
        }
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ArrowParquetReader));
    }
}
