using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using K4os.Compression.LZ4;
using Plank;
using Plank.Schema;
using Snappier;
using ZstdSharp;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {
        internal const int WriteStateEmpty = 0;
        internal const int WriteStateEncoded = 1;
        internal const int WriteStateWritten = 2;
        const int MaxPageHeaderBytes = 64;
        const int DefaultEncodedBufferBytes = 4 * 1024 * 1024;

        readonly RowGroupColumns _runtime;
        readonly RowGroupDrainState _drain;
        readonly RowGroupBufferCatalog _buffers;
        readonly DateTimeKindHandling _dateTimeKindHandling;
        readonly CompressionKind _compressionKind;
        readonly Compressor? _zstdCompressor;
        readonly byte[] _pageHeaderBuffer;
        internal RowGroupOptions Options;
        Dictionary<Column, int> _columnOrdinals
            => _runtime.ColumnOrdinals;
        Column[] _columns
            => _runtime.Columns;
        ColumnState[] _columnStates
            => _runtime.ColumnStates;
        TaskCompletionSource<bool>?[] _writeSignals
            => _runtime.WriteSignals;
        internal ColumnChunkMetadata[] ColumnMetadata
            => _runtime.ColumnMetadata;
        internal int RowCount
        {
            get => _runtime.RowCount;
            set => _runtime.RowCount = value;
        }
        internal int NextOrdinal
        {
            get => _runtime.NextOrdinal;
            set => _runtime.NextOrdinal = value;
        }
        internal int ColumnCount
            => _buffers.Count;

        internal RowGroupState(ImmutableArray<Column> columns, RowGroupOptions options, uint? rowGroupRowCountHint, DateTimeKindHandling dateTimeKindHandling, CompressionKind compressionKind, IBufferPool bufferPool)
        {
            _runtime = new RowGroupColumns(columns);
            _drain = new RowGroupDrainState();
            _dateTimeKindHandling = dateTimeKindHandling;
            _compressionKind = compressionKind;
            _buffers = new RowGroupBufferCatalog(_columns, options, rowGroupRowCountHint, _compressionKind, bufferPool);
            _zstdCompressor = compressionKind == CompressionKind.Zstd ? new Compressor(1) : null;
            _pageHeaderBuffer = new byte[MaxPageHeaderBytes];
            RowCount = -1;
            NextOrdinal = 0;
            _drain.InProgress = 0;
            _drain.CompletionSignaled = 0;
            Options = options;
            ConfigureBufferRequestIds(0);
        }

        internal void Reset(RowGroupOptions options)
        {
            Options = options;
            RowCount = -1;
            NextOrdinal = 0;
            _drain.InProgress = 0;
            _drain.CompletionSignaled = 0;
            for (var i = 0; i < _columnStates.Length; i++)
            {
                ref var state = ref _columnStates[i];
                ClearColumnDataMetrics(ref state);
                Volatile.Write(ref state.WriteState, WriteStateEmpty);
                state.Encoding = default;
                state.Compression = default;
                state.ExternalData = default;
                if (state.ExternalDataOwner is not null)
                {
                    state.ExternalDataOwner.Dispose();
                    state.ExternalDataOwner = null;
                }
                ColumnMetadata[i] = default;
                _writeSignals[i] = null;
            }
        }

        internal void ConfigureBufferRequestIds(long startId)
            => _buffers.ConfigureRequestIds(startId);

        static void ClearColumnDataMetrics(ref ColumnState state)
        {
            state.ValueCount = 0;
            state.RowCount = 0;
            state.EncodedLength = 0;
            state.UncompressedLength = 0;
            state.NullCount = 0;
            state.DefinitionLevelsByteLength = 0;
            state.RepetitionLevelsByteLength = 0;
            state.EncodeDurationTicks = 0;
            state.CompressionDurationTicks = 0;
            state.EncodedTimestampTicks = 0;
            ClearStringMetrics(ref state);
        }

        static void ClearStringMetrics(ref ColumnState state)
        {
            state.StringRowCount = 0;
            state.StringNonNullCount = 0;
            state.StringSizePassTicks = 0;
            state.StringDefinitionLevelsTicks = 0;
            state.StringUtf8WritePassTicks = 0;
        }

        internal int EncodeColumn<T>(Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType)
        {
            if (!_columnOrdinals.TryGetValue(column, out var ordinal))
                throw new ArgumentException("Column does not belong to this schema.", nameof(column));
            if (column.Options.Repetition is ParquetRepetition.Repeated)
                throw new InvalidOperationException($"Column '{column.Name}' is Repeated. Use WriteAsync(column, rows) instead.");
            if (column.PhysicalType != physicalType)
                throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");

            ref var state = ref _columnStates[ordinal];
            if (state.WriteState != WriteStateEmpty)
                throw new InvalidOperationException($"Column '{column.Name}' was already written.");

            if (state.EncodedBufferOwner is null)
                state.EncodedBufferOwner = _buffers.RentEncoded(ordinal);
            state.ValueCount = values.Length;
            state.RowCount = values.Length;
            var encodeStarted = Stopwatch.GetTimestamp();
            ColumnCodec.Encode(column, values, physicalType, _dateTimeKindHandling, ref state);
            var encodeCompleted = Stopwatch.GetTimestamp();
            var compressStarted = encodeCompleted;
            ColumnCodec.Compress(ref state, _compressionKind);
            var compressCompleted = Stopwatch.GetTimestamp();
            state.EncodeDurationTicks = encodeCompleted - encodeStarted;
            state.CompressionDurationTicks = compressCompleted - compressStarted;
            state.EncodedTimestampTicks = compressCompleted;
            Volatile.Write(ref state.WriteState, WriteStateEncoded);
            return ordinal;
        }

        internal IMemoryOwner<byte> RentSerializedBuffer(Column column)
        {
            ArgumentNullException.ThrowIfNull(column);
            if (!_columnOrdinals.TryGetValue(column, out var ordinal))
                throw new ArgumentException("Column does not belong to this schema.", nameof(column));

            return _buffers.RentEncoded(ordinal);
        }

        internal int EncodeRepeatedColumn<T>(Column column, ReadOnlySpan<T[]> rows, ParquetPhysicalType physicalType)
        {
            if (!_columnOrdinals.TryGetValue(column, out var ordinal))
                throw new ArgumentException("Column does not belong to this schema.", nameof(column));
            if (column.Options.Repetition is not ParquetRepetition.Repeated)
                throw new InvalidOperationException($"Column '{column.Name}' is not Repeated.");
            if (column.PhysicalType != physicalType)
                throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");

            ref var state = ref _columnStates[ordinal];
            if (state.WriteState != WriteStateEmpty)
                throw new InvalidOperationException($"Column '{column.Name}' was already written.");

            if (state.EncodedBufferOwner is null)
                state.EncodedBufferOwner = _buffers.RentEncoded(ordinal);
            state.RowCount = rows.Length;
            var encodeStarted = Stopwatch.GetTimestamp();
            ColumnCodec.EncodeRepeated(column, rows, physicalType, _dateTimeKindHandling, ref state);
            var encodeCompleted = Stopwatch.GetTimestamp();
            var compressStarted = encodeCompleted;
            ColumnCodec.Compress(ref state, _compressionKind);
            var compressCompleted = Stopwatch.GetTimestamp();
            state.EncodeDurationTicks = encodeCompleted - encodeStarted;
            state.CompressionDurationTicks = compressCompleted - compressStarted;
            state.EncodedTimestampTicks = compressCompleted;
            Volatile.Write(ref state.WriteState, WriteStateEncoded);
            return ordinal;
        }

        internal bool TryGetOrdinal(Column column, out int ordinal)
            => _columnOrdinals.TryGetValue(column, out ordinal);

        internal int AcceptSerialized(int ordinal, SerializedColumn serialized)
        {
            ref var state = ref _columnStates[ordinal];
            if (state.WriteState != WriteStateEmpty)
                throw new InvalidOperationException($"Column '{serialized.Column.Name}' was already written.");

            state.RowCount = serialized.RowCount;
            state.ValueCount = serialized.ValueCount;
            state.UncompressedLength = serialized.UncompressedLength;
            state.EncodedLength = serialized.Payload.Length;
            state.NullCount = serialized.NullCount;
            state.DefinitionLevelsByteLength = serialized.DefinitionLevelsByteLength;
            state.RepetitionLevelsByteLength = serialized.RepetitionLevelsByteLength;
            state.Encoding = serialized.Encoding;
            state.Compression = serialized.Compression;
            state.ExternalData = serialized.Payload;
            state.ExternalDataOwner = serialized.PayloadOwner;
            state.EncodeDurationTicks = 0;
            state.CompressionDurationTicks = 0;
            state.EncodedTimestampTicks = Stopwatch.GetTimestamp();
            Volatile.Write(ref state.WriteState, WriteStateEncoded);
            return ordinal;
        }

        internal ValueTask GetWriteTask(int ordinal, CancellationToken cancellationToken)
        {
            if (ordinal < 0 || ordinal >= _columnStates.Length)
                throw new ArgumentOutOfRangeException(nameof(ordinal));
            if (Volatile.Read(ref _columnStates[ordinal].WriteState) == WriteStateWritten)
                return ValueTask.CompletedTask;
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled(cancellationToken);

            var signal = _writeSignals[ordinal];
            if (signal is null)
            {
                signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _writeSignals[ordinal] = signal;
            }

            if (!cancellationToken.CanBeCanceled)
                return new ValueTask(signal.Task);

            return new ValueTask(signal.Task.WaitAsync(cancellationToken));
        }

        internal void TryDrain(ParquetWriter writer)
        {
            while (true)
            {
                if (Interlocked.CompareExchange(ref _drain.InProgress, 1, 0) != 0)
                    return;

                try
                {
                    while (NextOrdinal < _columnStates.Length && Volatile.Read(ref _columnStates[NextOrdinal].WriteState) == WriteStateEncoded)
                    {
                        WriteColumn(writer, NextOrdinal);
                        var signal = _writeSignals[NextOrdinal];
                        if (signal is not null)
                            signal.TrySetResult(true);
                        NextOrdinal++;
                    }

                    if (NextOrdinal == _columnStates.Length && Interlocked.Exchange(ref _drain.CompletionSignaled, 1) == 0)
                        writer.CompleteRowGroup();
                }
                finally
                {
                    Volatile.Write(ref _drain.InProgress, 0);
                }

                if (Volatile.Read(ref _drain.CompletionSignaled) != 0)
                    return;
                if ((uint)NextOrdinal >= (uint)_columnStates.Length)
                    return;
                if (Volatile.Read(ref _columnStates[NextOrdinal].WriteState) != WriteStateEncoded)
                    return;
            }
        }



        internal void ReleaseBuffers()
        {
            for (var i = 0; i < _columnStates.Length; i++)
            {
                ref var state = ref _columnStates[i];
                if (state.EncodedBufferOwner is not null)
                {
                    state.EncodedBufferOwner.Dispose();
                    state.EncodedBufferOwner = null;
                }

                if (state.CompressedBufferOwner is null)
                {
                    if (state.ExternalDataOwner is null)
                        continue;
                }
                else
                {
                    state.CompressedBufferOwner.Dispose();
                    state.CompressedBufferOwner = null;
                }

                if (state.ExternalDataOwner is null)
                    continue;
                state.ExternalDataOwner.Dispose();
                state.ExternalDataOwner = null;
            }

            _zstdCompressor?.Dispose();
        }

        internal struct ColumnState
        {
            internal int ValueCount;
            internal int RowCount;
            internal int EncodedLength;
            internal int UncompressedLength;
            internal int NullCount;
            internal int DefinitionLevelsByteLength;
            internal int RepetitionLevelsByteLength;
            internal int WriteState;
            internal byte[]? EncodedBuffer;
            internal IMemoryOwner<byte>? EncodedBufferOwner;
            internal IMemoryOwner<byte>? CompressedBufferOwner;
            internal ReadOnlyMemory<byte> ExternalData;
            internal IMemoryOwner<byte>? ExternalDataOwner;
            internal EncodingKind Encoding;
            internal CompressionKind Compression;
            internal long EncodeDurationTicks;
            internal long CompressionDurationTicks;
            internal long EncodedTimestampTicks;
            internal int StringRowCount;
            internal int StringNonNullCount;
            internal long StringSizePassTicks;
            internal long StringDefinitionLevelsTicks;
            internal long StringUtf8WritePassTicks;
        }
    }
}

