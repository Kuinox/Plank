using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using K4os.Compression.LZ4;
using Snappier;
using Plank.Schema;

namespace Plank.Writing;

public sealed partial class ParquetWriter : IDisposable
{
    Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly RowGroupState _rowGroupState;
    readonly PageCompressorSelector _pageCompressors;
    readonly GrowableBufferWriter _streamingCompressedBuffer;
    readonly Dictionary<Column, int> _columnOrdinals;
    readonly ColumnSemanticRegistry _semanticRegistry;
    readonly ColumnChunkMetadata[][] _rowGroupColumns;
    readonly RowGroupInfo[] _rowGroups;
    byte[] _footerBuffer;
    long _position;
    long _lastWriteEndTimestamp;
    bool _hasLastWriteEndTimestamp;
    int _activeColumnOrdinal;
    long _activeColumnWriteTicks;
    bool _rowGroupActive;
    bool _finalized;
    int _rowGroupCount;
    int _drainInProgress;
    int _drainCompletionSignaled;
    long _nextBufferRequestId;
    static readonly byte[] FileMagic = "PAR1"u8.ToArray();

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        if (options.ExpectedRowGroupCount is > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), options.ExpectedRowGroupCount.Value, "Expected row group count must fit in Int32.");

        var rowGroupCapacity = options.ExpectedRowGroupCount.HasValue ? checked((int)options.ExpectedRowGroupCount.Value) : 1;
        _rowGroups = rowGroupCapacity > 0 ? new RowGroupInfo[rowGroupCapacity] : [];

        ImmutableArray<Column> columns = schema.Columns.IsDefault ? [] : schema.Columns;
        _columnOrdinals = columns.Length > 0
            ? new Dictionary<Column, int>(columns.Length, ReferenceEqualityComparer.Instance)
            : new Dictionary<Column, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < columns.Length; i++)
            _columnOrdinals[columns[i]] = i;
        _semanticRegistry = new ColumnSemanticRegistry(columns.Length);
        _pageCompressors = new PageCompressorSelector();
        _streamingCompressedBuffer = new GrowableBufferWriter();
        _rowGroupColumns = rowGroupCapacity > 0 ? new ColumnChunkMetadata[rowGroupCapacity][] : [];
        if (_rowGroupColumns.Length > 0)
        {
            if (columns.Length == 0)
            {
                var empty = Array.Empty<ColumnChunkMetadata>();
                for (var i = 0; i < _rowGroupColumns.Length; i++)
                    _rowGroupColumns[i] = empty;
            }
            else
                for (var i = 0; i < _rowGroupColumns.Length; i++)
                    _rowGroupColumns[i] = new ColumnChunkMetadata[columns.Length];
        }

        _rowGroupState = new RowGroupState(columns, options, options.BufferPool ?? new NamedMemoryPool());
        _footerBuffer = options.FooterBufferBytes > 0
            ? new byte[options.FooterBufferBytes]
            : throw new ArgumentOutOfRangeException(nameof(options), options.FooterBufferBytes, "FooterBufferBytes must be positive.");
        _position = 0;
        _lastWriteEndTimestamp = 0;
        _hasLastWriteEndTimestamp = false;
        _activeColumnOrdinal = -1;
        _activeColumnWriteTicks = 0;
        _rowGroupCount = 0;
        _drainInProgress = 0;
        _drainCompletionSignaled = 0;
        _rowGroupActive = false;
        _finalized = false;
        _nextBufferRequestId = 0;
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
        _lastWriteEndTimestamp = 0;
        _hasLastWriteEndTimestamp = false;
        _activeColumnOrdinal = -1;
        _activeColumnWriteTicks = 0;
        _rowGroupCount = 0;
        _drainInProgress = 0;
        _drainCompletionSignaled = 0;
        _semanticRegistry.Clear();
        _rowGroupActive = false;
        _finalized = false;
        _nextBufferRequestId = 0;
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
        _drainInProgress = 0;
        _drainCompletionSignaled = 0;
        _rowGroupState.Reset();
        var rowGroupColumnCount = _rowGroupState.ConfigureAndGetColumnCount(_nextBufferRequestId);
        _nextBufferRequestId = checked(_nextBufferRequestId + rowGroupColumnCount);
        return new RowGroupWriter(this, _rowGroupState);
    }

    public SerializedColumn SerializeColumn<T>(Column column, ReadOnlySpan<T> values, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(destination);
        var physicalType = GetPhysicalType<T>();
        if (column.PhysicalType != physicalType)
            throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");

        var state = new RowGroupState.ColumnState
        {
            EncodedBuffer = destination
        };
        state.RowCount = values.Length;
        state.ValueCount = values.Length;
        Encoding.Encode(column, values, physicalType, _options.DateTimeKindHandling, ref state);
        CompressColumnPayload(column, ref state);
        var logicalType = ResolveSerializedLogicalType(typeof(T), column);
        return new SerializedColumn(
            column,
            state.EncodedBuffer.AsMemory(0, state.EncodedLength),
            state.RowCount,
            values.Length,
            state.UncompressedLength,
            state.NullCount,
            state.DefinitionLevelsByteLength,
            state.RepetitionLevelsByteLength,
            state.Encoding,
            state.Compression,
            logicalType,
            repeatedElementOptional: false,
            dataPayloadCompressed: state.DataPayloadCompressed);
    }

    public SerializedColumn SerializeColumn<T>(Column column, ReadOnlySpan<T> values)
    {
        ArgumentNullException.ThrowIfNull(column);
        var physicalType = GetPhysicalType<T>();
        if (column.PhysicalType != physicalType)
            throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {physicalType}.");
        var owner = _rowGroupState.RentSerializedBuffer(column);
        try
        {
            var state = new RowGroupState.ColumnState
            {
                EncodedBufferOwner = owner
            };
            state.RowCount = values.Length;
            state.ValueCount = values.Length;
            Encoding.Encode(column, values, physicalType, _options.DateTimeKindHandling, ref state);
            CompressColumnPayload(column, ref state);
            var logicalType = ResolveSerializedLogicalType(typeof(T), column);
            return new SerializedColumn(
                column,
                owner.Memory[..state.EncodedLength],
                state.RowCount,
                values.Length,
                state.UncompressedLength,
                state.NullCount,
                state.DefinitionLevelsByteLength,
                state.RepetitionLevelsByteLength,
                state.Encoding,
                state.Compression,
                logicalType,
                repeatedElementOptional: false,
                dataPayloadCompressed: state.DataPayloadCompressed,
                payloadOwner: owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public SerializedColumn SerializeColumn<T>(Column column, ReadOnlySpan<T[]> rows, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(destination);
        var state = new RowGroupState.ColumnState
        {
            EncodedBuffer = destination,
            RowCount = rows.Length
        };
        bool repeatedElementOptional;
        ColumnLogicalType logicalType;
        if (column.Options.Repetition is ParquetRepetition.Repeated)
        {
            var repeatedPhysicalType = GetPhysicalType<T>();
            if (column.PhysicalType != repeatedPhysicalType)
                throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {repeatedPhysicalType}.");

            Encoding.EncodeRepeated(column, rows, repeatedPhysicalType, _options.DateTimeKindHandling, ref state);
            logicalType = ResolveSerializedLogicalType(typeof(T), column);
            repeatedElementOptional = ColumnSemanticRegistry.IsRepeatedElementOptional(typeof(T));
        }
        else
        {
            var scalarPhysicalType = GetPhysicalType<T[]>();
            if (column.PhysicalType != scalarPhysicalType)
                throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {scalarPhysicalType}.");

            state.ValueCount = rows.Length;
            Encoding.Encode(column, rows, scalarPhysicalType, _options.DateTimeKindHandling, ref state);
            logicalType = ResolveSerializedLogicalType(typeof(T[]), column);
            repeatedElementOptional = false;
        }

        CompressColumnPayload(column, ref state);
        return new SerializedColumn(
            column,
            state.EncodedBuffer.AsMemory(0, state.EncodedLength),
            state.RowCount,
            state.ValueCount,
            state.UncompressedLength,
            state.NullCount,
            state.DefinitionLevelsByteLength,
            state.RepetitionLevelsByteLength,
            state.Encoding,
            state.Compression,
            logicalType,
            repeatedElementOptional,
            dataPayloadCompressed: state.DataPayloadCompressed);
    }

    public SerializedColumn SerializeColumn<T>(Column column, ReadOnlySpan<T[]> rows)
    {
        ArgumentNullException.ThrowIfNull(column);
        var owner = _rowGroupState.RentSerializedBuffer(column);
        try
        {
            var state = new RowGroupState.ColumnState
            {
                EncodedBufferOwner = owner,
                RowCount = rows.Length
            };
            bool repeatedElementOptional;
            ColumnLogicalType logicalType;
            if (column.Options.Repetition is ParquetRepetition.Repeated)
            {
                var repeatedPhysicalType = GetPhysicalType<T>();
                if (column.PhysicalType != repeatedPhysicalType)
                    throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {repeatedPhysicalType}.");

                Encoding.EncodeRepeated(column, rows, repeatedPhysicalType, _options.DateTimeKindHandling, ref state);
                logicalType = ResolveSerializedLogicalType(typeof(T), column);
                repeatedElementOptional = ColumnSemanticRegistry.IsRepeatedElementOptional(typeof(T));
            }
            else
            {
                var scalarPhysicalType = GetPhysicalType<T[]>();
                if (column.PhysicalType != scalarPhysicalType)
                    throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {scalarPhysicalType}.");

                state.ValueCount = rows.Length;
                Encoding.Encode(column, rows, scalarPhysicalType, _options.DateTimeKindHandling, ref state);
                logicalType = ResolveSerializedLogicalType(typeof(T[]), column);
                repeatedElementOptional = false;
            }

            CompressColumnPayload(column, ref state);
            return new SerializedColumn(
                column,
                owner.Memory[..state.EncodedLength],
                state.RowCount,
                state.ValueCount,
                state.UncompressedLength,
                state.NullCount,
                state.DefinitionLevelsByteLength,
                state.RepetitionLevelsByteLength,
                state.Encoding,
                state.Compression,
                logicalType,
                repeatedElementOptional,
                dataPayloadCompressed: state.DataPayloadCompressed,
                payloadOwner: owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
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
    {
        _rowGroupState.ReleaseBuffers();
        _streamingCompressedBuffer.Dispose();
        _pageCompressors.Dispose();
        _stream.Dispose();
    }

    internal void CompleteRowGroup()
    {
        var index = _rowGroupCount;
        if (index == _rowGroups.Length)
            throw new InvalidOperationException("Row group capacity exceeded. Set ExpectedRowGroupCount to preallocate sufficient capacity.");

        var destination = _rowGroupColumns[index];
        if (destination.Length > 0)
            _rowGroupState.CopyColumnMetadataTo(destination);
        _rowGroups[index] = new RowGroupInfo(_rowGroupState.GetRowCount(), destination);
        _rowGroupCount = index + 1;
        _rowGroupActive = false;
    }

    internal void AdvancePosition(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Byte count must be non-negative.");

        _position = checked(_position + byteCount);
    }

    void WriteFileHeader()
    {
        WriteToStream(FileMagic);
        AdvancePosition(FileMagic.Length);
    }

    void WriteFileFooter()
    {
        var metadataSize = ParquetThriftWriter.GetFileMetaDataSize(_schema, _semanticRegistry._logicalTypes, _semanticRegistry._states, _rowGroups, _rowGroupCount);
        var footerSize = checked(metadataSize + sizeof(int) + FileMagic.Length);
        if (footerSize > _footerBuffer.Length)
            throw new InvalidOperationException($"Footer requires {footerSize} bytes but FooterBufferBytes is {_footerBuffer.Length}.");

        ParquetThriftWriter.WriteFileMetaData(_footerBuffer.AsSpan(0, metadataSize), _schema, _semanticRegistry._logicalTypes, _semanticRegistry._states, _rowGroups, _rowGroupCount);
        BinaryPrimitives.WriteInt32LittleEndian(_footerBuffer.AsSpan(metadataSize, sizeof(int)), metadataSize);
        FileMagic.CopyTo(_footerBuffer.AsSpan(metadataSize + sizeof(int), FileMagic.Length));
        WriteToStream(_footerBuffer.AsSpan(0, footerSize));
        AdvancePosition(footerSize);
    }

    void WriteToStream(ReadOnlySpan<byte> payload)
    {
        if (ReferenceEquals(_options.Log, ParquetLog.None))
        {
            _stream.Write(payload);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var gapTicks = _hasLastWriteEndTimestamp ? started - _lastWriteEndTimestamp : 0;
        _stream.Write(payload);
        var completed = Stopwatch.GetTimestamp();
        if (_activeColumnOrdinal >= 0)
            _activeColumnWriteTicks = checked(_activeColumnWriteTicks + (completed - started));
        _lastWriteEndTimestamp = completed;
        _hasLastWriteEndTimestamp = true;
        _options.Log.StreamWriteObserved(payload.Length, completed - started, gapTicks);
    }

    internal void CompressColumnPayload(Column column, ref RowGroupState.ColumnState state)
    {
        var compression = _options.Compression;
        state.Compression = compression;
        state.DataPayloadCompressed = false;
        if (compression == CompressionKind.None)
            return;

        var levelsByteLength = checked(state.DefinitionLevelsByteLength + state.RepetitionLevelsByteLength);
        if (state.EncodedLength < levelsByteLength)
            throw new InvalidOperationException($"Column '{column.Name}' encoded payload is invalid.");

        var dataLength = state.EncodedLength - levelsByteLength;
        if (dataLength <= 0)
            return;

        var source = GetSerializedPayload(ref state);
        var data = source[levelsByteLength..state.EncodedLength];
        var compressor = _pageCompressors.Select(compression);
        ReadOnlySpan<byte> compressedData;
        byte[]? scratch = null;
        if (compressor.UsesStreamingOutput)
        {
            _streamingCompressedBuffer.Reset(_options.RowGroupOptions.MaxCompressedBytes);
            _ = compressor.Compress(data, _streamingCompressedBuffer);
            compressedData = _streamingCompressedBuffer.WrittenSpan;
        }
        else
        {
            var scratchLength = GetCompressionScratchLength(compression, dataLength, _options.RowGroupOptions.MaxCompressedBytes);
            scratch = ArrayPool<byte>.Shared.Rent(scratchLength);
            var written = compressor.Compress(data, scratch.AsSpan(0, scratchLength));
            compressedData = scratch.AsSpan(0, written);
        }

        try
        {
            var requiredLength = checked(levelsByteLength + compressedData.Length);
            var destination = EnsureSerializedDestinationCapacity(column, ref state, requiredLength, out var previousOwner);
            if (levelsByteLength > 0)
                source[..levelsByteLength].CopyTo(destination);
            compressedData.CopyTo(destination[levelsByteLength..]);
            previousOwner?.Dispose();
            state.EncodedLength = requiredLength;
            state.DataPayloadCompressed = true;
        }
        finally
        {
            if (scratch is not null)
                ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    static int GetCompressionScratchLength(CompressionKind compression, int uncompressedLength, int maxCompressedBytes)
    {
        var length = compression switch
        {
            CompressionKind.Snappy => Snappy.GetMaxCompressedLength(uncompressedLength),
            CompressionKind.Lz4 => LZ4Codec.MaximumOutputSize(uncompressedLength),
            CompressionKind.Gzip => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
            CompressionKind.Brotli => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
            CompressionKind.Zstd => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
            CompressionKind.None => uncompressedLength,
            _ => throw new NotSupportedException($"Compression '{compression}' is not supported yet.")
        };

        if (maxCompressedBytes > 0)
            length = Math.Min(length, maxCompressedBytes);
        if (length <= 0)
            throw new InvalidOperationException("Compressed payload exceeds MaxCompressedBytes.");
        return length;
    }

    static ReadOnlySpan<byte> GetSerializedPayload(ref RowGroupState.ColumnState state)
    {
        if (state.EncodedBuffer is not null)
            return state.EncodedBuffer.AsSpan();
        if (state.EncodedBufferOwner is not null)
            return state.EncodedBufferOwner.Memory.Span;
        throw new InvalidOperationException("Serialized payload buffer is missing.");
    }

    static Span<byte> EnsureSerializedDestinationCapacity(Column column, ref RowGroupState.ColumnState state, int requiredLength,
        out IMemoryOwner<byte>? previousOwner)
    {
        previousOwner = null;
        if (state.EncodedBuffer is not null)
        {
            if (state.EncodedBuffer.Length < requiredLength)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' compressed payload requires {requiredLength} bytes but destination capacity is {state.EncodedBuffer.Length}.");
            return state.EncodedBuffer.AsSpan(0, requiredLength);
        }

        if (state.EncodedBufferOwner is null)
            throw new InvalidOperationException("Serialized payload buffer is missing.");
        if (state.EncodedBufferOwner.Memory.Length >= requiredLength)
            return state.EncodedBufferOwner.Memory.Span[..requiredLength];

        previousOwner = state.EncodedBufferOwner;
        state.EncodedBufferOwner = MemoryPool<byte>.Shared.Rent(requiredLength);
        return state.EncodedBufferOwner.Memory.Span[..requiredLength];
    }

    internal void BeginColumnWriteTiming(int ordinal)
    {
        _activeColumnOrdinal = ordinal;
        _activeColumnWriteTicks = 0;
    }

    internal long EndColumnWriteTiming(int ordinal)
    {
        if (_activeColumnOrdinal != ordinal)
            return 0;
        var ticks = _activeColumnWriteTicks;
        _activeColumnOrdinal = -1;
        _activeColumnWriteTicks = 0;
        return ticks;
    }

    internal void RegisterValueType(Column column, Type valueType)
    {
        if (!_columnOrdinals.TryGetValue(column, out var ordinal))
            throw new ArgumentException("Column does not belong to this schema.", nameof(column));
        _semanticRegistry.RegisterValueType(ordinal, column, valueType);
    }

    internal void RegisterSerializedColumnType(int ordinal, Column column, ColumnLogicalType logicalType, bool repeatedElementOptional)
        => _semanticRegistry.RegisterSerializedColumnType(ordinal, column, logicalType, repeatedElementOptional);

    static ColumnLogicalType ResolveSerializedLogicalType(Type valueType, Column column)
        => ColumnSemanticRegistry.ResolveSerializedLogicalType(valueType, column);

    static ParquetPhysicalType GetPhysicalType<T>()
        => ParquetTypeMap.GetPhysicalType<T>();

    static byte GetInt64SerializedState(ColumnLogicalType logicalType)
        => (byte)ColumnSemanticRegistry.GetInt64SerializedState(logicalType);


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
}
