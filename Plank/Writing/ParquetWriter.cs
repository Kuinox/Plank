using System.Collections.Generic;
using System.Collections.Immutable;
using Plank;
using Plank.Schema;

namespace Plank.Writing;

public sealed class ParquetWriter : IDisposable, IAsyncDisposable
{
    readonly Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly uint? _expectedRowGroupCount;
    readonly IParquetLog _log;
    readonly RowGroupState _rowGroupState;
    bool _rowGroupActive;
    RowGroupInfo[] _rowGroups;
    int _rowGroupCount;

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options, uint? expectedRowGroupCount)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        _expectedRowGroupCount = expectedRowGroupCount;
        _log = options.Log;
        var capacity = checked((int)(expectedRowGroupCount ?? 0));
        _rowGroups = capacity > 0 ? new RowGroupInfo[capacity] : [];
        _rowGroupCount = 0;
        var columns = schema.Columns.IsDefault ? ImmutableArray<Column>.Empty : schema.Columns;
        _rowGroupState = new RowGroupState(columns);
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
    {
        if (_rowGroupActive)
            throw new InvalidOperationException("A row group is already active for this writer.");

        _rowGroupActive = true;
        _rowGroupState.Reset(options ?? RowGroupOptions.Default);
        return new RowGroupWriter(this, _rowGroupState);
    }

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
        _rowGroupActive = false;
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

    internal sealed class RowGroupState
    {
        internal readonly ImmutableArray<Column> SchemaColumns;
        internal readonly Dictionary<Column, int> ColumnOrdinals;
        internal readonly ColumnState[] ColumnStates;
        internal int RowCount;
        internal int NextOrdinal;
        internal RowGroupOptions Options;

        internal RowGroupState(ImmutableArray<Column> columns)
        {
            SchemaColumns = columns;
            var count = columns.Length;
            ColumnStates = count > 0 ? new ColumnState[count] : [];
            ColumnOrdinals = count > 0
                ? new Dictionary<Column, int>(count, ReferenceEqualityComparer.Instance)
                : new Dictionary<Column, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < count; i++)
                ColumnOrdinals[columns[i]] = i;
            RowCount = -1;
            NextOrdinal = 0;
            Options = RowGroupOptions.Default;
        }

        internal void Reset(RowGroupOptions options)
        {
            Options = options;
            RowCount = -1;
            NextOrdinal = 0;
            var targetLength = options.MaxEncodedBytes;
            for (var i = 0; i < ColumnStates.Length; i++)
            {
                ref var state = ref ColumnStates[i];
                if (targetLength > 0 && (state.EncodedBuffer is null || state.EncodedBuffer.Length < targetLength))
                    state.EncodedBuffer = new byte[targetLength];

                state.ValueCount = 0;
                state.EncodedLength = 0;
                state.Encoding = default;
                state.Compression = default;
            }
        }

        internal struct ColumnState
        {
            internal int ValueCount;
            internal int EncodedLength;
            internal byte[]? EncodedBuffer;
            internal EncodingKind Encoding;
            internal CompressionKind Compression;
        }
    }
}
