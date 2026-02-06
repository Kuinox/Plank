using System.Collections.Immutable;
using Plank;
using Plank.Schema;

namespace Plank.Writing;

public sealed partial class ParquetWriter : IDisposable
{
    Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly uint? _expectedRowGroupCount;
    readonly uint? _rowGroupRowCountHint;
    readonly IParquetLog _log;
    readonly RowGroupState _rowGroupState;
    byte[] _footerBuffer;
    ColumnChunkMetadata[][] _rowGroupColumns;
    long _position;
    bool _rowGroupActive;
    bool _finalized;
    RowGroupInfo[] _rowGroups;
    int _rowGroupCount;
    static readonly byte[] FileMagic = "PAR1"u8.ToArray();

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        _expectedRowGroupCount = options.ExpectedRowGroupCount;
        _rowGroupRowCountHint = options.RowGroupRowCountHint;
        _log = options.Log;
        if (_expectedRowGroupCount is > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), _expectedRowGroupCount.Value, "Expected row group count must fit in Int32.");
        var capacity = _expectedRowGroupCount.HasValue ? checked((int)_expectedRowGroupCount.Value) : 1;
        _rowGroups = capacity > 0 ? new RowGroupInfo[capacity] : [];
        ImmutableArray<Column> columns = schema.Columns.IsDefault ? [] : schema.Columns;
        _rowGroupColumns = capacity > 0 ? new ColumnChunkMetadata[capacity][] : [];
        if (_rowGroupColumns.Length > 0)
        {
            if (columns.Length == 0)
            {
                var empty = Array.Empty<ColumnChunkMetadata>();
                for (var i = 0; i < _rowGroupColumns.Length; i++)
                    _rowGroupColumns[i] = empty;
            }
            else
            {
                for (var i = 0; i < _rowGroupColumns.Length; i++)
                    _rowGroupColumns[i] = new ColumnChunkMetadata[columns.Length];
            }
        }
        _rowGroupCount = 0;
        _rowGroupState = new RowGroupState(columns, options.RowGroupOptions, _rowGroupRowCountHint);
        _footerBuffer = options.FooterBufferBytes > 0 ? new byte[options.FooterBufferBytes] : throw new ArgumentOutOfRangeException(nameof(options), options.FooterBufferBytes, "FooterBufferBytes must be positive.");
        _position = 0;
        _finalized = false;
        WriteFileHeader();
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default);
    }

    public void Reset(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_rowGroupActive)
            throw new InvalidOperationException("Cannot reset while a row group is active.");

        _stream = stream;
        _position = 0;
        _rowGroupCount = 0;
        _rowGroupActive = false;
        _finalized = false;
        WriteFileHeader();
    }

    public RowGroupWriter StartRowGroup(RowGroupOptions? options = null)
    {
        if (_finalized)
            throw new InvalidOperationException("Cannot start a new row group after the file has been finalized. Call Reset(stream) to write another file.");

        if (_rowGroupActive)
            throw new InvalidOperationException("A row group is already active for this writer.");

        if (options is not null && !ReferenceEquals(options, _options.RowGroupOptions))
            throw new InvalidOperationException("RowGroupOptions cannot be changed when reuse/no-allocation guarantees are required.");

        _rowGroupActive = true;
        _rowGroupState.Reset(options ?? _options.RowGroupOptions);
        return new RowGroupWriter(this, _rowGroupState);
    }

    public void CloseFile(CancellationToken cancellationToken = default)
    {
        if (_finalized)
            return;

        if (_rowGroupActive)
            throw new InvalidOperationException("Cannot close the file while a row group is still open.");

        cancellationToken.ThrowIfCancellationRequested();
        WriteFileFooter();
        _finalized = true;
    }

    public void Dispose()
        => _stream.Dispose();
}
