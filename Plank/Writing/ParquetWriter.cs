using Plank.Schema;
using Plank;

namespace Plank.Writing;

public sealed class ParquetWriter : IDisposable, IAsyncDisposable
{
    readonly Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly uint _expectedRowGroupCount;
    readonly IParquetLog _log;
    RowGroupInfo[] _rowGroups;
    int _rowGroupCount;

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options, uint expectedRowGroupCount)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        _expectedRowGroupCount = expectedRowGroupCount;
        _log = options.Log;
        _rowGroups = expectedRowGroupCount > 0 ? new RowGroupInfo[expectedRowGroupCount] : [];
        _rowGroupCount = 0;
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default, -1);
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, uint rowGroupCount, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default, rowGroupCount);
    }

    public RowGroupWriter StartRowGroup(RowGroupOptions? options = null)
        => new RowGroupWriter(this, options ?? RowGroupOptions.Default);

    internal Stream Stream => _stream;

    internal ParquetSchema Schema => _schema;

    internal ParquetWriterOptions Options => _options;

    internal void CompleteRowGroup(int rowCount)
    {
        var index = Interlocked.Increment(ref _rowGroupCount) - 1;
        EnsureRowGroupCapacity(index + 1);
        _rowGroups[index] = new RowGroupInfo(rowCount);
    }

    void EnsureRowGroupCapacity(int minCapacity)
    {
        while (true)
        {
            var current = _rowGroups;
            if (current.Length >= minCapacity)
                return;

            var previous = current.Length;
            var newCapacity = previous == 0 ? Math.Max(1, minCapacity) : Math.Max(previous * 2, minCapacity);
            var grown = new RowGroupInfo[newCapacity];
            Array.Copy(current, grown, previous);

            if (!ReferenceEquals(Interlocked.CompareExchange(ref _rowGroups, grown, current), current))
                continue;

            if (_expectedRowGroupCount < 0)
                _log.RowGroupMetadataCapacityGrown(previous, newCapacity, null);
            else
                _log.RowGroupMetadataCapacityGrown(previous, newCapacity, _expectedRowGroupCount);
            return;
        }
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
