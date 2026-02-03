using Plank.Schema;
using Plank;

namespace Plank.Writing;

public sealed class ParquetWriter : IDisposable, IAsyncDisposable
{
    readonly Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly int _expectedRowGroupCount;
    readonly IParquetLog _log;
    readonly object _sync;
    RowGroupInfo[] _rowGroups;
    int _rowGroupCount;

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options, int expectedRowGroupCount)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        _expectedRowGroupCount = expectedRowGroupCount;
        _log = options.Log;
        _sync = new object();
        _rowGroups = expectedRowGroupCount > 0 ? new RowGroupInfo[expectedRowGroupCount] : [];
        _rowGroupCount = 0;
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default, -1);
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, int rowGroupCount, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        if (rowGroupCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroupCount), rowGroupCount, "Row group count must be non-negative.");

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default, rowGroupCount);
    }

    public RowGroupWriter StartRowGroup(RowGroupOptions? options = null)
        => new RowGroupWriter(this, options ?? RowGroupOptions.Default);

    internal Stream Stream => _stream;

    internal ParquetSchema Schema => _schema;

    internal ParquetWriterOptions Options => _options;

    internal void CompleteRowGroup(int rowCount)
    {
        lock (_sync)
        {
            var index = _rowGroupCount;
            if (index == _rowGroups.Length)
                GrowRowGroupCapacity(index + 1);

            _rowGroups[index] = new RowGroupInfo(rowCount);
            _rowGroupCount = index + 1;
        }
    }

    void GrowRowGroupCapacity(int minCapacity)
    {
        var previous = _rowGroups.Length;
        var newCapacity = previous == 0 ? Math.Max(1, minCapacity) : Math.Max(previous * 2, minCapacity);
        var grown = new RowGroupInfo[newCapacity];
        Array.Copy(_rowGroups, grown, previous);
        _rowGroups = grown;

        if (_expectedRowGroupCount < 0)
            _log.RowGroupMetadataCapacityGrown(previous, newCapacity, null);
        else
            _log.RowGroupMetadataCapacityGrown(previous, newCapacity, _expectedRowGroupCount);
    }

    public void Dispose()
        => _stream.Dispose();

    public ValueTask DisposeAsync()
        => _stream.DisposeAsync();

    readonly struct RowGroupInfo
    {
        public RowGroupInfo(int rowCount)
        {
            RowCount = rowCount;
        }

        public int RowCount { get; }
    }
}
