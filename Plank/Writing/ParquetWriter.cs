using System.Buffers.Binary;
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
    readonly uint? _rowGroupRowCountHint;
    readonly IParquetLog _log;
    readonly RowGroupState _rowGroupState;
    long _position;
    bool _rowGroupActive;
    bool _completed;
    bool _headerWritten;
    Task? _headerWriteTask;
    readonly object _headerSync;
    RowGroupInfo[] _rowGroups;
    int _rowGroupCount;
    static readonly byte[] FileMagic = [(byte)'P', (byte)'A', (byte)'R', (byte)'1'];

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        _expectedRowGroupCount = options.ExpectedRowGroupCount;
        _rowGroupRowCountHint = options.RowGroupRowCountHint;
        _log = options.Log;
        if (_expectedRowGroupCount.HasValue && _expectedRowGroupCount.Value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), _expectedRowGroupCount.Value, "Expected row group count must fit in Int32.");
        var capacity = _expectedRowGroupCount.HasValue ? checked((int)_expectedRowGroupCount.Value) : 0;
        _rowGroups = capacity > 0 ? new RowGroupInfo[capacity] : [];
        _rowGroupCount = 0;
        var columns = schema.Columns.IsDefault ? ImmutableArray<Column>.Empty : schema.Columns;
        _rowGroupState = new RowGroupState(columns);
        _position = 0;
        _completed = false;
        _headerWritten = false;
        _headerWriteTask = null;
        _headerSync = new object();
    }

    public static ParquetWriter Create(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);

        return new ParquetWriter(stream, schema, options ?? ParquetWriterOptions.Default);
    }

    public RowGroupWriter StartRowGroup(RowGroupOptions? options = null)
    {
        if (_completed)
            throw new InvalidOperationException("Cannot start a row group after the writer is completed.");

        if (_rowGroupActive)
            throw new InvalidOperationException("A row group is already active for this writer.");

        _rowGroupActive = true;
        _rowGroupState.Reset(options ?? RowGroupOptions.Default, _rowGroupRowCountHint);
        return new RowGroupWriter(this, _rowGroupState);
    }

    internal Stream Stream => _stream;

    internal ParquetSchema Schema => _schema;

    internal ParquetWriterOptions Options => _options;

    internal long Position => _position;

    internal void AdvancePosition(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Byte count must be non-negative.");

        _position = checked(_position + byteCount);
    }

    internal void CompleteRowGroup(int rowCount)
    {
        var index = _rowGroupCount;
        if (index == _rowGroups.Length)
            GrowRowGroupCapacity(index + 1);
        _rowGroups[index] = new RowGroupInfo(rowCount, SnapshotColumnMetadata(_rowGroupState.ColumnMetadata));
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

    public void Complete()
        => throw new NotSupportedException("Synchronous completion is not supported. Use CompleteAsync instead.");

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
            return;

        if (_rowGroupActive)
            throw new InvalidOperationException("Cannot complete a writer with an active row group.");

        await EnsureHeaderWrittenAsync(cancellationToken).ConfigureAwait(false);
        await WriteFileFooterAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public void Dispose()
        => _stream.Dispose();

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    static ColumnChunkMetadata[] SnapshotColumnMetadata(ColumnChunkMetadata[] source)
    {
        if (source.Length == 0)
            return [];

        var snapshot = new ColumnChunkMetadata[source.Length];
        Array.Copy(source, snapshot, source.Length);
        return snapshot;
    }

    async ValueTask WriteFileFooterAsync(CancellationToken cancellationToken)
    {
        var metadata = ParquetThriftWriter.CreateFileMetaDataBytes(_schema, _rowGroups, _rowGroupCount);
        await _stream.WriteAsync(metadata.AsMemory(), cancellationToken).ConfigureAwait(false);
        AdvancePosition(metadata.Length);

        var footerLength = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(footerLength, metadata.Length);
        await _stream.WriteAsync(footerLength.AsMemory(), cancellationToken).ConfigureAwait(false);
        AdvancePosition(footerLength.Length);

        await _stream.WriteAsync(FileMagic.AsMemory(), cancellationToken).ConfigureAwait(false);
        AdvancePosition(FileMagic.Length);
    }

    internal ValueTask EnsureHeaderWrittenAsync(CancellationToken cancellationToken)
    {
        if (_headerWritten)
            return ValueTask.CompletedTask;

        Task? writeTask = Volatile.Read(ref _headerWriteTask);
        if (writeTask is null)
        {
            lock (_headerSync)
            {
                if (_headerWritten)
                    return ValueTask.CompletedTask;

                if (_headerWriteTask is null)
                    _headerWriteTask = WriteHeaderAsyncCore();

                writeTask = _headerWriteTask;
            }
        }

        if (!cancellationToken.CanBeCanceled)
            return new ValueTask(writeTask);

        return new ValueTask(writeTask.WaitAsync(cancellationToken));
    }

    async Task WriteHeaderAsyncCore()
    {
        await _stream.WriteAsync(FileMagic.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        AdvancePosition(FileMagic.Length);
        _headerWritten = true;
    }

    internal readonly struct ColumnChunkMetadata
    {
        public ColumnChunkMetadata(long offset, int valueCount, long totalUncompressedSize, long totalCompressedSize, EncodingKind encoding, CompressionKind compression)
        {
            Offset = offset;
            ValueCount = valueCount;
            TotalUncompressedSize = totalUncompressedSize;
            TotalCompressedSize = totalCompressedSize;
            Encoding = encoding;
            Compression = compression;
        }

        public long Offset { get; }

        public int ValueCount { get; }

        public long TotalUncompressedSize { get; }

        public long TotalCompressedSize { get; }

        public EncodingKind Encoding { get; }

        public CompressionKind Compression { get; }
    }

    internal readonly struct RowGroupInfo
    {
        public RowGroupInfo(int rowCount, ColumnChunkMetadata[] columns)
        {
            RowCount = rowCount;
            Columns = columns;
        }

        public int RowCount { get; }

        public ColumnChunkMetadata[] Columns { get; }
    }

    internal sealed class RowGroupState
    {
        internal const int WriteStateEmpty = 0;
        internal const int WriteStateEncoded = 1;
        internal const int WriteStateWritten = 2;
        const int MaxPageHeaderBytes = 64;

        internal readonly ImmutableArray<Column> SchemaColumns;
        internal readonly Dictionary<Column, int> ColumnOrdinals;
        internal readonly ColumnState[] ColumnStates;
        internal readonly ColumnChunkMetadata[] ColumnMetadata;
        internal readonly byte[] PageHeaderBuffer;
        internal int RowCount;
        internal int NextOrdinal;
        internal RowGroupOptions Options;
        readonly object _sync;
        TaskCompletionSource<bool>?[]? _writeSignals;

        internal RowGroupState(ImmutableArray<Column> columns)
        {
            SchemaColumns = columns;
            var count = columns.Length;
            ColumnStates = count > 0 ? new ColumnState[count] : [];
            ColumnMetadata = count > 0 ? new ColumnChunkMetadata[count] : [];
            ColumnOrdinals = count > 0
                ? new Dictionary<Column, int>(count, ReferenceEqualityComparer.Instance)
                : new Dictionary<Column, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < count; i++)
                ColumnOrdinals[columns[i]] = i;
            PageHeaderBuffer = new byte[MaxPageHeaderBytes];
            _sync = new object();
            RowCount = -1;
            NextOrdinal = 0;
            Options = RowGroupOptions.Default;
        }

        internal void Reset(RowGroupOptions options, uint? rowGroupRowCountHint)
        {
            Options = options;
            RowCount = -1;
            NextOrdinal = 0;
            if (_writeSignals is not null)
                Array.Clear(_writeSignals);
            var targetLength = options.MaxEncodedBytes;
            var rowCountHint = rowGroupRowCountHint;
            for (var i = 0; i < ColumnStates.Length; i++)
            {
                ref var state = ref ColumnStates[i];
                var length = targetLength;
                if (rowCountHint.HasValue && ColumnCodec.TryGetFixedWidthBytes(SchemaColumns[i].PhysicalType, out var width))
                {
                    var hintLength = checked((int)rowCountHint.Value * width);
                    if (hintLength > length)
                        length = hintLength;
                }

                if (length > 0 && (state.EncodedBuffer is null || state.EncodedBuffer.Length < length))
                    state.EncodedBuffer = new byte[length];

                state.ValueCount = 0;
                state.EncodedLength = 0;
                state.UncompressedLength = 0;
                state.WriteState = WriteStateEmpty;
                state.Encoding = default;
                state.Compression = default;
                ColumnMetadata[i] = default;
            }
        }

        internal ValueTask WaitForTurnAsync(int ordinal, CancellationToken cancellationToken)
        {
            if (ordinal == NextOrdinal)
                return ValueTask.CompletedTask;

            Task? waitTask;
            lock (_sync)
            {
                if (ordinal == NextOrdinal)
                    return ValueTask.CompletedTask;

                if (ordinal < NextOrdinal)
                    throw new InvalidOperationException($"Column ordinal {ordinal} was already written.");

                _writeSignals ??= new TaskCompletionSource<bool>[ColumnStates.Length];
                var signal = _writeSignals[ordinal];
                if (signal is null)
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _writeSignals[ordinal] = signal;
                }

                waitTask = signal.Task;
            }

            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled(cancellationToken);

            if (!cancellationToken.CanBeCanceled)
                return new ValueTask(waitTask);

            return new ValueTask(waitTask.WaitAsync(cancellationToken));
        }

        internal int AdvanceOrdinal()
        {
            TaskCompletionSource<bool>? signal = null;
            lock (_sync)
            {
                NextOrdinal++;
                if (_writeSignals is not null && NextOrdinal < _writeSignals.Length)
                {
                    signal = _writeSignals[NextOrdinal];
                    _writeSignals[NextOrdinal] = null;
                }
            }

            signal?.TrySetResult(true);
            return NextOrdinal;
        }

        internal struct ColumnState
        {
            internal int ValueCount;
            internal int EncodedLength;
            internal int UncompressedLength;
            internal int WriteState;
            internal byte[]? EncodedBuffer;
            internal EncodingKind Encoding;
            internal CompressionKind Compression;
        }
    }
}
