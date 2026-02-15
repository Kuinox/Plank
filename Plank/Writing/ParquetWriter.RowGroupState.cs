using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using Plank.Schema;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {
        const int WriteStateEmpty = 0;
        const int WriteStateEncoded = 1;
        const int WriteStateWritten = 2;
        const int MaxPageHeaderBytes = 64;
        const int DefaultEncodedBufferBytes = 4 * 1024 * 1024;

        readonly RowGroupColumnStore _columnStore;
        readonly RowGroupBufferCatalog _buffers;
        readonly ParquetWriterOptions _options;
        readonly byte[] _pageHeaderBuffer;

        internal RowGroupState(ImmutableArray<Column> columns, ParquetWriterOptions options, IBufferPool bufferPool)
        {
            _columnStore = new RowGroupColumnStore(columns);
            _options = options;
            _buffers = new RowGroupBufferCatalog(_columnStore.Schema.Columns, options.RowGroupOptions, options.RowGroupRowCountHint, options.Compression, bufferPool);
            _pageHeaderBuffer = new byte[MaxPageHeaderBytes];
            _columnStore.Progress.RowCount = -1;
            _columnStore.Progress.NextOrdinal = 0;
            _buffers.ConfigureRequestIds(0);
        }

        internal void Reset()
        {
            _columnStore.Progress.RowCount = -1;
            _columnStore.Progress.NextOrdinal = 0;
            for (var i = 0; i < _columnStore.Data.ColumnStates.Length; i++)
            {
                ref var state = ref _columnStore.Data.ColumnStates[i];
                ClearColumnDataMetrics(ref state);
                Volatile.Write(ref state.WriteState, WriteStateEmpty);
                state.Encoding = default;
                state.Compression = default;
                state.DataPayloadCompressed = false;
                state.ExternalData = default;
                if (state.ExternalDataOwner is not null)
                {
                    state.ExternalDataOwner.Dispose();
                    state.ExternalDataOwner = null;
                }
                _columnStore.Data.ColumnMetadata[i] = default;
                _columnStore.Write.Signals[i] = null;
            }
        }

        internal int ConfigureAndGetColumnCount(long startId)
        {
            _buffers.ConfigureRequestIds(startId);
            return _columnStore.Schema.Columns.Length;
        }

        internal int GetRowCount()
            => _columnStore.Progress.RowCount;

        internal void CopyColumnMetadataTo(ColumnChunkMetadata[] destination)
            => Array.Copy(_columnStore.Data.ColumnMetadata, destination, destination.Length);

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

        internal int EncodeColumn<T>(ParquetWriter writer, Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType)
        {
            if (!_columnStore.Schema.ColumnOrdinals.TryGetValue(column, out var ordinal))
                throw new ArgumentException("Column does not belong to this schema.", nameof(column));
            if (column.Options.Repetition is ParquetRepetition.Repeated)
                throw new InvalidOperationException($"Column '{column.Name}' is Repeated. Use WriteAsync(column, rows) instead.");
            if (column.PhysicalType != physicalType)
                throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");

            ref var state = ref _columnStore.Data.ColumnStates[ordinal];
            if (state.WriteState != WriteStateEmpty)
                throw new InvalidOperationException($"Column '{column.Name}' was already written.");

            if (state.EncodedBufferOwner is null)
                state.EncodedBufferOwner = _buffers.RentEncoded(ordinal);
            state.ValueCount = values.Length;
            state.RowCount = values.Length;
            var encodeStarted = Stopwatch.GetTimestamp();
            Encoding.Encode(column, values, physicalType, _options.DateTimeKindHandling, ref state);
            var encodeCompleted = Stopwatch.GetTimestamp();
            var compressStarted = encodeCompleted;
            writer.CompressColumnPayload(column, ref state);
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
            if (!_columnStore.Schema.ColumnOrdinals.TryGetValue(column, out var ordinal))
                throw new ArgumentException("Column does not belong to this schema.", nameof(column));

            return _buffers.RentEncoded(ordinal);
        }

        internal int EncodeRepeatedColumn<T>(ParquetWriter writer, Column column, ReadOnlySpan<T[]> rows, ParquetPhysicalType physicalType)
        {
            if (!_columnStore.Schema.ColumnOrdinals.TryGetValue(column, out var ordinal))
                throw new ArgumentException("Column does not belong to this schema.", nameof(column));
            if (column.Options.Repetition is not ParquetRepetition.Repeated)
                throw new InvalidOperationException($"Column '{column.Name}' is not Repeated.");
            if (column.PhysicalType != physicalType)
                throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");

            ref var state = ref _columnStore.Data.ColumnStates[ordinal];
            if (state.WriteState != WriteStateEmpty)
                throw new InvalidOperationException($"Column '{column.Name}' was already written.");

            if (state.EncodedBufferOwner is null)
                state.EncodedBufferOwner = _buffers.RentEncoded(ordinal);
            state.RowCount = rows.Length;
            var encodeStarted = Stopwatch.GetTimestamp();
            Encoding.EncodeRepeated(column, rows, physicalType, _options.DateTimeKindHandling, ref state);
            var encodeCompleted = Stopwatch.GetTimestamp();
            var compressStarted = encodeCompleted;
            writer.CompressColumnPayload(column, ref state);
            var compressCompleted = Stopwatch.GetTimestamp();
            state.EncodeDurationTicks = encodeCompleted - encodeStarted;
            state.CompressionDurationTicks = compressCompleted - compressStarted;
            state.EncodedTimestampTicks = compressCompleted;
            Volatile.Write(ref state.WriteState, WriteStateEncoded);
            return ordinal;
        }

        internal bool TryGetOrdinal(Column column, out int ordinal)
            => _columnStore.Schema.ColumnOrdinals.TryGetValue(column, out ordinal);

        internal int AcceptSerialized(int ordinal, SerializedColumn serialized)
        {
            ref var state = ref _columnStore.Data.ColumnStates[ordinal];
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
            state.DataPayloadCompressed = serialized.DataPayloadCompressed;
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
            if (ordinal < 0 || ordinal >= _columnStore.Data.ColumnStates.Length)
                throw new ArgumentOutOfRangeException(nameof(ordinal));
            if (Volatile.Read(ref _columnStore.Data.ColumnStates[ordinal].WriteState) == WriteStateWritten)
                return ValueTask.CompletedTask;
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled(cancellationToken);

            var signal = _columnStore.Write.Signals[ordinal];
            if (signal is null)
            {
                signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _columnStore.Write.Signals[ordinal] = signal;
            }

            if (!cancellationToken.CanBeCanceled)
                return new ValueTask(signal.Task);

            return new ValueTask(signal.Task.WaitAsync(cancellationToken));
        }

        internal void TryDrain(ParquetWriter writer)
        {
            while (true)
            {
                if (Interlocked.CompareExchange(ref writer._drainInProgress, 1, 0) != 0)
                    return;

                try
                {
                    while (_columnStore.Progress.NextOrdinal < _columnStore.Data.ColumnStates.Length && Volatile.Read(ref _columnStore.Data.ColumnStates[_columnStore.Progress.NextOrdinal].WriteState) == WriteStateEncoded)
                    {
                        WriteColumn(writer, _columnStore.Progress.NextOrdinal);
                        var signal = _columnStore.Write.Signals[_columnStore.Progress.NextOrdinal];
                        signal?.TrySetResult(true);
                        _columnStore.Progress.NextOrdinal++;
                    }

                    if (_columnStore.Progress.NextOrdinal == _columnStore.Data.ColumnStates.Length && Interlocked.Exchange(ref writer._drainCompletionSignaled, 1) == 0)
                        writer.CompleteRowGroup();
                }
                finally
                {
                    Volatile.Write(ref writer._drainInProgress, 0);
                }

                if (Volatile.Read(ref writer._drainCompletionSignaled) != 0)
                    return;
                if ((uint)_columnStore.Progress.NextOrdinal >= (uint)_columnStore.Data.ColumnStates.Length)
                    return;
                if (Volatile.Read(ref _columnStore.Data.ColumnStates[_columnStore.Progress.NextOrdinal].WriteState) != WriteStateEncoded)
                    return;
            }
        }



        internal void ReleaseBuffers()
        {
            for (var i = 0; i < _columnStore.Data.ColumnStates.Length; i++)
            {
                ref var state = ref _columnStore.Data.ColumnStates[i];
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
            internal bool DataPayloadCompressed;
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
