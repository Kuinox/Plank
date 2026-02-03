using Plank.Schema;
using Plank;

namespace Plank.Writing;

public sealed class ParquetWriter : IDisposable, IAsyncDisposable
{
    readonly Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly uint? _expectedRowGroupCount;
    readonly IParquetLog _log;
    RowGroupInfo[] _rowGroups;
    int _rowGroupCount;

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options, uint? expectedRowGroupCount)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        _expectedRowGroupCount = expectedRowGroupCount;
        _log = options.Log;
        var capacity = expectedRowGroupCount.HasValue ? checked((int)expectedRowGroupCount.Value) : 0;
        _rowGroups = capacity > 0 ? new RowGroupInfo[capacity] : [];
        _rowGroupCount = 0;
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default, null);
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, uint rowGroupCount, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        if (rowGroupCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rowGroupCount), rowGroupCount, "Row group count must fit in Int32.");

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default, rowGroupCount);
    }

    public RowGroupWriter StartRowGroup(RowGroupOptions? options = null)
        => new RowGroupWriter(this, options ?? RowGroupOptions.Default);

    internal Stream Stream => _stream;

    internal ParquetSchema Schema => _schema;

    internal ParquetWriterOptions Options => _options;

    internal void CompleteRowGroup(int rowCount)
    {
        var index = _rowGroupCount;
        if (index == _rowGroups.Length)
            GrowRowGroupCapacity(index + 1);
        _rowGroups[index] = new RowGroupInfo(rowCount);
        _rowGroupCount = index + 1;
    }

    void GrowRowGroupCapacity(int minCapacity)
    {
        var previous = _rowGroups.Length;
        var newCapacity = previous == 0 ? Math.Max(1, minCapacity) : Math.Max(previous * 2, minCapacity);
        var grown = new RowGroupInfo[newCapacity];
        Array.Copy(_rowGroups, grown, previous);
        _rowGroups = grown;

        if (_expectedRowGroupCount.HasValue)
            _log.RowGroupMetadataCapacityGrown(previous, newCapacity, checked((int)_expectedRowGroupCount.Value));
        else
            _log.RowGroupMetadataCapacityGrown(previous, newCapacity, null);
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
