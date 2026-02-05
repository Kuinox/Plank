using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks.Sources;
using Plank;
using Plank.Schema;

namespace Plank.Writing;

public sealed class ParquetWriter : IDisposable, IAsyncDisposable
{
    Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly uint? _expectedRowGroupCount;
    readonly uint? _rowGroupRowCountHint;
    readonly IParquetLog _log;
    readonly RowGroupState _rowGroupState;
    readonly byte[] _footerBuffer;
    readonly ColumnChunkMetadata[][] _rowGroupColumns;
    long _position;
    bool _rowGroupActive;
    bool _completed;
    bool _headerWritten;
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
        var capacity = _expectedRowGroupCount.HasValue ? checked((int)_expectedRowGroupCount.Value) : 1;
        _rowGroups = capacity > 0 ? new RowGroupInfo[capacity] : [];
        var columns = schema.Columns.IsDefault ? ImmutableArray<Column>.Empty : schema.Columns;
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
        _completed = false;
        _headerWritten = false;
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
        _completed = false;
        _headerWritten = false;
    }

    public RowGroupWriter StartRowGroup(RowGroupOptions? options = null)
    {
        if (_completed)
            throw new InvalidOperationException("Cannot start a row group after the writer is completed.");

        if (_rowGroupActive)
            throw new InvalidOperationException("A row group is already active for this writer.");

        if (options is not null && !ReferenceEquals(options, _options.RowGroupOptions))
            throw new InvalidOperationException("RowGroupOptions cannot be changed when reuse/no-allocation guarantees are required.");

        _rowGroupActive = true;
        _rowGroupState.Reset(options ?? _options.RowGroupOptions);
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
            throw new InvalidOperationException("Row group capacity exceeded. Set ExpectedRowGroupCount to preallocate sufficient capacity.");
        var destination = _rowGroupColumns[index];
        if (destination.Length > 0)
            Array.Copy(_rowGroupState.ColumnMetadata, destination, destination.Length);
        _rowGroups[index] = new RowGroupInfo(rowCount, destination);
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

    ValueTask WriteFileFooterAsync(CancellationToken cancellationToken)
    {
        var metadataSize = ParquetThriftWriter.GetFileMetaDataSize(_schema, _rowGroups, _rowGroupCount);
        var footerSize = checked(metadataSize + sizeof(int) + FileMagic.Length);
        if (footerSize > _footerBuffer.Length)
            throw new InvalidOperationException($"Footer requires {footerSize} bytes but FooterBufferBytes is {_footerBuffer.Length}.");

        ParquetThriftWriter.WriteFileMetaData(_footerBuffer.AsSpan(0, metadataSize), _schema, _rowGroups, _rowGroupCount);
        BinaryPrimitives.WriteInt32LittleEndian(_footerBuffer.AsSpan(metadataSize, sizeof(int)), metadataSize);
        FileMagic.CopyTo(_footerBuffer.AsSpan(metadataSize + sizeof(int), FileMagic.Length));
        AdvancePosition(footerSize);
        return _stream.WriteAsync(_footerBuffer.AsMemory(0, footerSize), cancellationToken);
    }

    internal ValueTask EnsureHeaderWrittenAsync(CancellationToken cancellationToken)
    {
        if (_headerWritten)
            return ValueTask.CompletedTask;

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        _headerWritten = true;
        AdvancePosition(FileMagic.Length);
        return _stream.WriteAsync(FileMagic.AsMemory(), cancellationToken);
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
        readonly AsyncOrdinalGate[] _ordinalGates;

        internal RowGroupState(ImmutableArray<Column> columns, RowGroupOptions options, uint? rowGroupRowCountHint)
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
            _ordinalGates = count > 0 ? new AsyncOrdinalGate[count] : [];
            for (var i = 0; i < _ordinalGates.Length; i++)
                _ordinalGates[i] = new AsyncOrdinalGate();
            InitializeBuffers(options, rowGroupRowCountHint);
            RowCount = -1;
            NextOrdinal = 0;
            Options = RowGroupOptions.Default;
        }

        internal void Reset(RowGroupOptions options)
        {
            Options = options;
            RowCount = -1;
            NextOrdinal = 0;
            for (var i = 0; i < _ordinalGates.Length; i++)
                _ordinalGates[i].Reset();
            for (var i = 0; i < ColumnStates.Length; i++)
            {
                ref var state = ref ColumnStates[i];
                if (state.EncodedBuffer is null)
                    throw new InvalidOperationException($"Column '{SchemaColumns[i].Name}' was not preallocated.");

                state.ValueCount = 0;
                state.EncodedLength = 0;
                state.UncompressedLength = 0;
                state.WriteState = WriteStateEmpty;
                state.Encoding = default;
                state.Compression = default;
                ColumnMetadata[i] = default;
            }
        }

        void InitializeBuffers(RowGroupOptions options, uint? rowGroupRowCountHint)
        {
            var targetLength = options.MaxEncodedBytes;
            var rowCountHint = rowGroupRowCountHint;
            for (var i = 0; i < ColumnStates.Length; i++)
            {
                var length = targetLength;
                if (rowCountHint.HasValue && ColumnCodec.TryGetFixedWidthBytes(SchemaColumns[i].PhysicalType, out var width))
                {
                    var hintLength = checked((int)rowCountHint.Value * width);
                    if (hintLength > length)
                        length = hintLength;
                }

                if (length > 0)
                    ColumnStates[i].EncodedBuffer = new byte[length];
            }
        }

        internal ValueTask WaitForTurnAsync(int ordinal, CancellationToken cancellationToken)
        {
            var nextOrdinal = Volatile.Read(ref NextOrdinal);
            if (ordinal == nextOrdinal)
                return ValueTask.CompletedTask;

            if (ordinal < nextOrdinal)
                throw new InvalidOperationException($"Column ordinal {ordinal} was already written.");

            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled(cancellationToken);

            return _ordinalGates[ordinal].WaitAsync();
        }

        internal int AdvanceOrdinal()
        {
            var nextOrdinal = Interlocked.Increment(ref NextOrdinal);
            if ((uint)nextOrdinal < (uint)_ordinalGates.Length)
                _ordinalGates[nextOrdinal].Set();
            return nextOrdinal;
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

        sealed class AsyncOrdinalGate : IValueTaskSource
        {
            ManualResetValueTaskSourceCore<bool> _core;
            int _signaled;

            internal AsyncOrdinalGate()
            {
                _core = new ManualResetValueTaskSourceCore<bool>
                {
                    RunContinuationsAsynchronously = true
                };
            }

            internal void Reset()
            {
                _signaled = 0;
                _core.Reset();
            }

            internal void Set()
            {
                if (Interlocked.Exchange(ref _signaled, 1) != 0)
                    return;

                _core.SetResult(true);
            }

            internal ValueTask WaitAsync()
            {
                if (Volatile.Read(ref _signaled) != 0)
                    return ValueTask.CompletedTask;

                return new ValueTask(this, _core.Version);
            }

            void IValueTaskSource.GetResult(short token)
                => _core.GetResult(token);

            ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
                => _core.GetStatus(token);

            void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
                => _core.OnCompleted(continuation, state, token, flags);
        }
    }
}
