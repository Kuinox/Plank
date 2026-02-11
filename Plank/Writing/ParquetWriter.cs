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

public sealed class ParquetWriter : IDisposable
{
    Stream _stream;
    readonly ParquetSchema _schema;
    readonly ParquetWriterOptions _options;
    readonly DateTimeKindHandling _dateTimeKindHandling;
    readonly RowGroupState _rowGroupState;
    readonly IBufferPool _bufferPool;
    readonly Dictionary<Column, int> _columnOrdinals;
    readonly byte[] _int32SemanticStates;
    readonly byte[] _int64SemanticStates;
    readonly byte[] _byteArraySemanticStates;
    readonly byte[] _repeatedElementStates;
    readonly ColumnLogicalType[] _columnLogicalTypes;
    readonly ColumnChunkMetadata[][] _rowGroupColumns;
    readonly RowGroupInfo[] _rowGroups;
    readonly IParquetLog _log;
    readonly bool _streamWriteMetricsEnabled;
    byte[] _footerBuffer;
    long _position;
    long _lastWriteEndTimestamp;
    bool _hasLastWriteEndTimestamp;
    int _activeColumnOrdinal;
    long _activeColumnWriteTicks;
    bool _rowGroupActive;
    bool _finalized;
    int _rowGroupCount;
    static readonly byte[] FileMagic = "PAR1"u8.ToArray();

    ParquetWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions options)
    {
        _stream = stream;
        _schema = schema;
        _options = options;
        _log = options.Log;
        _streamWriteMetricsEnabled = !ReferenceEquals(_log, ParquetLog.None);
        _dateTimeKindHandling = options.DateTimeKindHandling;
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
        _int32SemanticStates = columns.Length > 0 ? new byte[columns.Length] : [];
        _int64SemanticStates = columns.Length > 0 ? new byte[columns.Length] : [];
        _byteArraySemanticStates = columns.Length > 0 ? new byte[columns.Length] : [];
        _repeatedElementStates = columns.Length > 0 ? new byte[columns.Length] : [];
        _columnLogicalTypes = columns.Length > 0 ? new ColumnLogicalType[columns.Length] : [];
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

        _bufferPool = options.BufferPool ?? new NamedMemoryPool();
        _rowGroupState = new RowGroupState(columns, options.RowGroupOptions, options.RowGroupRowCountHint, _dateTimeKindHandling, options.Compression, _bufferPool);
        _footerBuffer = options.FooterBufferBytes > 0
            ? new byte[options.FooterBufferBytes]
            : throw new ArgumentOutOfRangeException(nameof(options), options.FooterBufferBytes, "FooterBufferBytes must be positive.");
        _position = 0;
        _lastWriteEndTimestamp = 0;
        _hasLastWriteEndTimestamp = false;
        _activeColumnOrdinal = -1;
        _activeColumnWriteTicks = 0;
        _rowGroupCount = 0;
        _rowGroupActive = false;
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
        _lastWriteEndTimestamp = 0;
        _hasLastWriteEndTimestamp = false;
        _activeColumnOrdinal = -1;
        _activeColumnWriteTicks = 0;
        _rowGroupCount = 0;
        if (_int32SemanticStates.Length > 0)
            Array.Clear(_int32SemanticStates);
        if (_int64SemanticStates.Length > 0)
            Array.Clear(_int64SemanticStates);
        if (_byteArraySemanticStates.Length > 0)
            Array.Clear(_byteArraySemanticStates);
        if (_repeatedElementStates.Length > 0)
            Array.Clear(_repeatedElementStates);
        if (_columnLogicalTypes.Length > 0)
            Array.Clear(_columnLogicalTypes);
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
        ColumnCodec.Encode(column, values, physicalType, _dateTimeKindHandling, ref state);
        ColumnCodec.Compress(ref state, _options.Compression);
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
            repeatedElementOptional: false);
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
            ColumnCodec.Encode(column, values, physicalType, _dateTimeKindHandling, ref state);
            ColumnCodec.Compress(ref state, _options.Compression);
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

            ColumnCodec.EncodeRepeated(column, rows, repeatedPhysicalType, _dateTimeKindHandling, ref state);
            logicalType = ResolveSerializedLogicalType(typeof(T), column);
            repeatedElementOptional = GetRepeatedElementState(typeof(T)) == 2;
        }
        else
        {
            var scalarPhysicalType = GetPhysicalType<T[]>();
            if (column.PhysicalType != scalarPhysicalType)
                throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {scalarPhysicalType}.");

            state.ValueCount = rows.Length;
            ColumnCodec.Encode(column, rows, scalarPhysicalType, _dateTimeKindHandling, ref state);
            logicalType = ResolveSerializedLogicalType(typeof(T[]), column);
            repeatedElementOptional = false;
        }

        ColumnCodec.Compress(ref state, _options.Compression);
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
            repeatedElementOptional);
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

                ColumnCodec.EncodeRepeated(column, rows, repeatedPhysicalType, _dateTimeKindHandling, ref state);
                logicalType = ResolveSerializedLogicalType(typeof(T), column);
                repeatedElementOptional = GetRepeatedElementState(typeof(T)) == 2;
            }
            else
            {
                var scalarPhysicalType = GetPhysicalType<T[]>();
                if (column.PhysicalType != scalarPhysicalType)
                    throw new InvalidOperationException($"Column '{column.Name}' expects {column.PhysicalType}, but received {scalarPhysicalType}.");

                state.ValueCount = rows.Length;
                ColumnCodec.Encode(column, rows, scalarPhysicalType, _dateTimeKindHandling, ref state);
                logicalType = ResolveSerializedLogicalType(typeof(T[]), column);
                repeatedElementOptional = false;
            }

            ColumnCodec.Compress(ref state, _options.Compression);
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
        _stream.Dispose();
    }

    internal void CompleteRowGroup()
    {
        var index = _rowGroupCount;
        if (index == _rowGroups.Length)
            throw new InvalidOperationException("Row group capacity exceeded. Set ExpectedRowGroupCount to preallocate sufficient capacity.");

        var destination = _rowGroupColumns[index];
        if (destination.Length > 0)
            Array.Copy(_rowGroupState.ColumnMetadata, destination, destination.Length);
        _rowGroups[index] = new RowGroupInfo(_rowGroupState.RowCount, destination);
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
        var metadataSize = ParquetThriftWriter.GetFileMetaDataSize(_schema, _columnLogicalTypes, _repeatedElementStates, _rowGroups, _rowGroupCount);
        var footerSize = checked(metadataSize + sizeof(int) + FileMagic.Length);
        if (footerSize > _footerBuffer.Length)
            throw new InvalidOperationException($"Footer requires {footerSize} bytes but FooterBufferBytes is {_footerBuffer.Length}.");

        ParquetThriftWriter.WriteFileMetaData(_footerBuffer.AsSpan(0, metadataSize), _schema, _columnLogicalTypes, _repeatedElementStates, _rowGroups, _rowGroupCount);
        BinaryPrimitives.WriteInt32LittleEndian(_footerBuffer.AsSpan(metadataSize, sizeof(int)), metadataSize);
        FileMagic.CopyTo(_footerBuffer.AsSpan(metadataSize + sizeof(int), FileMagic.Length));
        WriteToStream(_footerBuffer.AsSpan(0, footerSize));
        AdvancePosition(footerSize);
    }

    void WriteToStream(ReadOnlySpan<byte> payload)
    {
        if (!_streamWriteMetricsEnabled)
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
        _log.StreamWriteObserved(payload.Length, completed - started, gapTicks);
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
        if (column.Options.Repetition is ParquetRepetition.Repeated)
            RegisterSemanticState(ordinal, _repeatedElementStates, GetRepeatedElementState(valueType), column.Name);
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
                RegisterSemanticState(ordinal, _int32SemanticStates, GetInt32SemanticState(valueType), column.Name);
                if (_int32SemanticStates[ordinal] == 2)
                    _columnLogicalTypes[ordinal] = ColumnLogicalType.Date;
                break;
            case ParquetPhysicalType.Int64:
                RegisterSemanticState(ordinal, _int64SemanticStates, GetInt64SemanticState(valueType), column.Name);
                if (_int64SemanticStates[ordinal] == 2)
                    _columnLogicalTypes[ordinal] = ColumnLogicalType.TimestampMicrosUtc;
                if (_int64SemanticStates[ordinal] == 3)
                    _columnLogicalTypes[ordinal] = ColumnLogicalType.TimeMicros;
                break;
            case ParquetPhysicalType.ByteArray:
                RegisterSemanticState(ordinal, _byteArraySemanticStates, GetByteArraySemanticState(valueType), column.Name);
                if (_byteArraySemanticStates[ordinal] == 2)
                    _columnLogicalTypes[ordinal] = ColumnLogicalType.Utf8;
                break;
        }
    }

    internal void RegisterSerializedColumnType(SerializedColumn serialized)
    {
        if (!_columnOrdinals.TryGetValue(serialized.Column, out var ordinal))
            throw new ArgumentException("Column does not belong to this schema.", nameof(serialized));
        if (serialized.Column.Options.Repetition is ParquetRepetition.Repeated)
            RegisterSemanticState(ordinal, _repeatedElementStates, serialized.RepeatedElementOptional ? (byte)2 : (byte)1, serialized.Column.Name);
        switch (serialized.Column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
                RegisterSemanticState(ordinal, _int32SemanticStates, serialized.LogicalType == ColumnLogicalType.Date ? (byte)2 : (byte)1, serialized.Column.Name);
                break;
            case ParquetPhysicalType.Int64:
                RegisterSemanticState(ordinal, _int64SemanticStates, GetInt64SerializedState(serialized.LogicalType), serialized.Column.Name);
                break;
            case ParquetPhysicalType.ByteArray:
                RegisterSemanticState(ordinal, _byteArraySemanticStates, serialized.LogicalType == ColumnLogicalType.Utf8 ? (byte)2 : (byte)1, serialized.Column.Name);
                break;
            default:
                return;
        }
        _columnLogicalTypes[ordinal] = serialized.LogicalType;
    }

    static ColumnLogicalType ResolveSerializedLogicalType(Type valueType, Column column)
    {
        if (column.PhysicalType == ParquetPhysicalType.Int64)
            return valueType == typeof(DateTime) || valueType == typeof(DateTimeOffset)
                ? ColumnLogicalType.TimestampMicrosUtc
                : valueType == typeof(TimeOnly)
                    ? ColumnLogicalType.TimeMicros
                    : ColumnLogicalType.None;
        if (column.PhysicalType == ParquetPhysicalType.Int32)
            return valueType == typeof(DateOnly)
                ? ColumnLogicalType.Date
                : ColumnLogicalType.None;
        if (column.PhysicalType == ParquetPhysicalType.ByteArray)
            return valueType == typeof(string) ? ColumnLogicalType.Utf8 : ColumnLogicalType.None;
        return ColumnLogicalType.None;
    }

    static ParquetPhysicalType GetPhysicalType<T>()
    {
        try
        {
            return ParquetTypeMap.GetPhysicalType<T>();
        }
        catch (TypeInitializationException ex) when (ex.InnerException is NotSupportedException inner)
        {
            throw inner;
        }
    }

    static byte GetInt32SemanticState(Type valueType)
    {
        if (valueType == typeof(DateOnly))
            return 2;
        if (valueType == typeof(int))
            return 1;
        return 0;
    }

    static byte GetInt64SemanticState(Type valueType)
    {
        if (valueType == typeof(DateTime) || valueType == typeof(DateTimeOffset))
            return 2;
        if (valueType == typeof(TimeOnly))
            return 3;
        if (valueType == typeof(long))
            return 1;
        return 0;
    }

    static byte GetInt64SerializedState(ColumnLogicalType logicalType)
        => logicalType switch
        {
            ColumnLogicalType.TimestampMicrosUtc => 2,
            ColumnLogicalType.TimeMicros => 3,
            _ => 1
        };

    static byte GetByteArraySemanticState(Type valueType)
    {
        if (valueType == typeof(string))
            return 2;
        if (valueType == typeof(byte[]))
            return 1;
        return 0;
    }

    static byte GetRepeatedElementState(Type valueType)
    {
        if (!valueType.IsValueType)
            return 2;
        if (Nullable.GetUnderlyingType(valueType) is not null)
            return 2;
        return 1;
    }

    static void RegisterSemanticState(int ordinal, byte[] states, byte desiredState, string columnName)
    {
        if (desiredState == 0)
            return;

        var existing = states[ordinal];
        if (existing == 0)
        {
            states[ordinal] = desiredState;
            return;
        }

        if (existing != desiredState)
            throw new InvalidOperationException($"Column '{columnName}' mixes incompatible logical semantics for its physical type.");
    }

    public readonly struct SerializedColumn : IEquatable<SerializedColumn>
    {
        internal SerializedColumn(Column column, ReadOnlyMemory<byte> payload, int rowCount, int valueCount, int uncompressedLength, int nullCount, int definitionLevelsByteLength, int repetitionLevelsByteLength, EncodingKind encoding, CompressionKind compression, ColumnLogicalType logicalType, bool repeatedElementOptional, IMemoryOwner<byte>? payloadOwner = null)
        {
            Column = column;
            Payload = payload;
            RowCount = rowCount;
            ValueCount = valueCount;
            UncompressedLength = uncompressedLength;
            NullCount = nullCount;
            DefinitionLevelsByteLength = definitionLevelsByteLength;
            RepetitionLevelsByteLength = repetitionLevelsByteLength;
            Encoding = encoding;
            Compression = compression;
            LogicalType = logicalType;
            RepeatedElementOptional = repeatedElementOptional;
            PayloadOwner = payloadOwner;
        }

        public Column Column { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public int RowCount { get; }

        public int ValueCount { get; }

        public int UncompressedLength { get; }

        internal int NullCount { get; }

        internal int DefinitionLevelsByteLength { get; }

        internal int RepetitionLevelsByteLength { get; }

        public EncodingKind Encoding { get; }

        public CompressionKind Compression { get; }

        internal ColumnLogicalType LogicalType { get; }

        internal bool RepeatedElementOptional { get; }

        internal IMemoryOwner<byte>? PayloadOwner { get; }

        public bool Equals(SerializedColumn other)
            => ReferenceEquals(Column, other.Column)
               && Payload.Equals(other.Payload)
               && RowCount == other.RowCount
               && ValueCount == other.ValueCount
               && UncompressedLength == other.UncompressedLength
               && NullCount == other.NullCount
               && DefinitionLevelsByteLength == other.DefinitionLevelsByteLength
               && RepetitionLevelsByteLength == other.RepetitionLevelsByteLength
               && Encoding == other.Encoding
               && Compression == other.Compression
               && LogicalType == other.LogicalType
               && RepeatedElementOptional == other.RepeatedElementOptional
               && ReferenceEquals(PayloadOwner, other.PayloadOwner);

        public override bool Equals(object? obj)
            => obj is SerializedColumn other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Column);
            hash.Add(Payload);
            hash.Add(RowCount);
            hash.Add(ValueCount);
            hash.Add(UncompressedLength);
            hash.Add(NullCount);
            hash.Add(DefinitionLevelsByteLength);
            hash.Add(RepetitionLevelsByteLength);
            hash.Add(Encoding);
            hash.Add(Compression);
            hash.Add(LogicalType);
            hash.Add(RepeatedElementOptional);
            hash.Add(PayloadOwner);
            return hash.ToHashCode();
        }

        public static bool operator ==(SerializedColumn left, SerializedColumn right)
            => left.Equals(right);

        public static bool operator !=(SerializedColumn left, SerializedColumn right)
            => !left.Equals(right);
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
        const int DefaultEncodedBufferBytes = 4 * 1024 * 1024;

        readonly Dictionary<Column, int> _columnOrdinals;
        readonly Column[] _columns;
        readonly ColumnState[] _columnStates;
        readonly TaskCompletionSource<bool>?[] _writeSignals;
        readonly string[] _bufferNames;
        readonly int[] _bufferLengths;
        readonly string[] _compressedBufferNames;
        readonly int[] _compressedBufferLengths;
        readonly DateTimeKindHandling _dateTimeKindHandling;
        readonly CompressionKind _compressionKind;
        readonly IBufferPool _bufferPool;
        readonly Compressor? _zstdCompressor;
        readonly byte[] _pageHeaderBuffer;
        readonly byte[] _levelDecodeScratch;
        int _drainInProgress;
        int _rowGroupCompletionSignaled;
        internal readonly ColumnChunkMetadata[] ColumnMetadata;
        internal int RowCount;
        internal int NextOrdinal;
        internal RowGroupOptions Options;

        internal RowGroupState(ImmutableArray<Column> columns, RowGroupOptions options, uint? rowGroupRowCountHint, DateTimeKindHandling dateTimeKindHandling, CompressionKind compressionKind, IBufferPool bufferPool)
        {
            _columns = columns.IsDefault ? [] : columns.ToArray();
            _columnStates = _columns.Length > 0 ? new ColumnState[_columns.Length] : [];
            ColumnMetadata = _columns.Length > 0 ? new ColumnChunkMetadata[_columns.Length] : [];
            _writeSignals = _columns.Length > 0 ? new TaskCompletionSource<bool>?[_columns.Length] : [];
            _bufferNames = _columns.Length > 0 ? new string[_columns.Length] : [];
            _bufferLengths = _columns.Length > 0 ? new int[_columns.Length] : [];
            _compressedBufferNames = _columns.Length > 0 ? new string[_columns.Length] : [];
            _compressedBufferLengths = _columns.Length > 0 ? new int[_columns.Length] : [];
            _columnOrdinals = _columns.Length > 0
                ? new Dictionary<Column, int>(_columns.Length, ReferenceEqualityComparer.Instance)
                : new Dictionary<Column, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < _columns.Length; i++)
                _columnOrdinals[_columns[i]] = i;

            _dateTimeKindHandling = dateTimeKindHandling;
            _compressionKind = compressionKind;
            _bufferPool = bufferPool;
            _zstdCompressor = compressionKind == CompressionKind.Zstd ? new Compressor(1) : null;
            _pageHeaderBuffer = new byte[MaxPageHeaderBytes];
            _levelDecodeScratch = [];
            RegisterBuffers(options, rowGroupRowCountHint);
            RowCount = -1;
            NextOrdinal = 0;
            _drainInProgress = 0;
            _rowGroupCompletionSignaled = 0;
            Options = options;
        }

        internal void Reset(RowGroupOptions options)
        {
            Options = options;
            RowCount = -1;
            NextOrdinal = 0;
            _drainInProgress = 0;
            _rowGroupCompletionSignaled = 0;
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
                state.EncodedBufferOwner = _bufferPool.Rent(_bufferNames[ordinal], _bufferLengths[ordinal]);
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

            return _bufferPool.Rent(_bufferNames[ordinal], _bufferLengths[ordinal]);
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
                state.EncodedBufferOwner = _bufferPool.Rent(_bufferNames[ordinal], _bufferLengths[ordinal]);
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

        internal int AcceptSerialized(SerializedColumn serialized)
        {
            ArgumentNullException.ThrowIfNull(serialized.Column);
            if (!_columnOrdinals.TryGetValue(serialized.Column, out var ordinal))
                throw new ArgumentException("Column does not belong to this schema.", nameof(serialized));

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
                if (Interlocked.CompareExchange(ref _drainInProgress, 1, 0) != 0)
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

                    if (NextOrdinal == _columnStates.Length && Interlocked.Exchange(ref _rowGroupCompletionSignaled, 1) == 0)
                        writer.CompleteRowGroup();
                }
                finally
                {
                    Volatile.Write(ref _drainInProgress, 0);
                }

                if (Volatile.Read(ref _rowGroupCompletionSignaled) != 0)
                    return;
                if ((uint)NextOrdinal >= (uint)_columnStates.Length)
                    return;
                if (Volatile.Read(ref _columnStates[NextOrdinal].WriteState) != WriteStateEncoded)
                    return;
            }
        }

        void WriteColumn(ParquetWriter writer, int ordinal)
        {
            ref var state = ref _columnStates[ordinal];
            var rowCount = state.RowCount;
            var valueCount = state.ValueCount;
            if (RowCount < 0)
                RowCount = rowCount;
            if (RowCount != rowCount)
                throw new InvalidOperationException($"Column ordinal {ordinal} has {rowCount} rows but row group expects {RowCount}.");

            var writeStarted = Stopwatch.GetTimestamp();
            var waitForWriteTicks = state.EncodedTimestampTicks > 0 ? Math.Max(0, writeStarted - state.EncodedTimestampTicks) : 0;
            writer.BeginColumnWriteTiming(ordinal);
            var offset = writer._position;
            long totalUncompressedSize;
            long totalCompressedSize;
            var plan = SelectPageWritePlan(writer, ordinal, ref state);
            ExecutePageWritePlan(writer, ordinal, ref state, plan, out totalUncompressedSize, out totalCompressedSize);
            var writeTicks = writer.EndColumnWriteTiming(ordinal);
            var bytesWritten = checked((int)(writer._position - offset));
            writer._log.ColumnWriteMetricsObserved(
                _columns[ordinal].Name,
                rowCount,
                valueCount,
                bytesWritten,
                state.EncodeDurationTicks,
                state.CompressionDurationTicks,
                waitForWriteTicks,
                writeTicks);
            if (state.StringRowCount > 0)
                writer._log.StringEncodingMetricsObserved(
                    _columns[ordinal].Name,
                    state.StringRowCount,
                    state.StringNonNullCount,
                    state.StringSizePassTicks,
                    state.StringDefinitionLevelsTicks,
                    0,
                    state.StringUtf8WritePassTicks);

            ColumnMetadata[ordinal] = new ColumnChunkMetadata(offset, state.ValueCount, totalUncompressedSize, totalCompressedSize, state.Encoding, state.Compression);
            state.ExternalData = default;
            if (state.ExternalDataOwner is not null)
            {
                state.ExternalDataOwner.Dispose();
                state.ExternalDataOwner = null;
            }
            ClearColumnDataMetrics(ref state);
            Volatile.Write(ref state.WriteState, WriteStateWritten);
        }

        readonly struct PageWritePlan
        {
            internal PageWritePlan(PageWriteMode mode, int bytesPerValue, int splitValueCount)
            {
                Mode = mode;
                BytesPerValue = bytesPerValue;
                SplitValueCount = splitValueCount;
            }

            internal PageWriteMode Mode { get; }
            internal int BytesPerValue { get; }
            internal int SplitValueCount { get; }
        }

        enum PageWriteMode
        {
            SinglePage,
            SplitFixedWidthRequired,
            SplitLevelFixedWidth,
            SplitVariableWidthRequired
        }

        PageWritePlan SelectPageWritePlan(ParquetWriter writer, int ordinal, ref ColumnState state)
        {
            if (TryCreateFixedWidthRequiredSplitPlan(ordinal, ref state, out var splitValueCount, out var bytesPerValue))
                return new PageWritePlan(PageWriteMode.SplitFixedWidthRequired, bytesPerValue, splitValueCount);
            if (TryCreateLevelFixedWidthSplitPlan(writer, ordinal, ref state, out bytesPerValue))
                return new PageWritePlan(PageWriteMode.SplitLevelFixedWidth, bytesPerValue, splitValueCount: 0);
            if (CanUseVariableWidthRequiredSplit(ordinal, ref state))
                return new PageWritePlan(PageWriteMode.SplitVariableWidthRequired, bytesPerValue: 0, splitValueCount: 0);

            return new PageWritePlan(PageWriteMode.SinglePage, bytesPerValue: 0, splitValueCount: 0);
        }

        void ExecutePageWritePlan(ParquetWriter writer, int ordinal, ref ColumnState state, PageWritePlan plan,
            out long totalUncompressedSize, out long totalCompressedSize)
        {
            switch (plan.Mode)
            {
                case PageWriteMode.SplitFixedWidthRequired:
                    WriteSplitPages(writer, ordinal, ref state, plan.SplitValueCount, plan.BytesPerValue,
                        out totalUncompressedSize, out totalCompressedSize);
                    break;
                case PageWriteMode.SplitLevelFixedWidth:
                    WriteSplitLevelFixedWidthPages(writer, ordinal, ref state, plan.BytesPerValue,
                        out totalUncompressedSize, out totalCompressedSize);
                    break;
                case PageWriteMode.SplitVariableWidthRequired:
                    WriteSplitVariableWidthPages(writer, ordinal, ref state, out totalUncompressedSize,
                        out totalCompressedSize);
                    break;
                default:
                    WriteSinglePage(writer, ordinal, ref state, out totalUncompressedSize, out totalCompressedSize);
                    break;
            }
        }

        bool TryCreateFixedWidthRequiredSplitPlan(int ordinal, ref ColumnState state, out int splitValueCount, out int bytesPerValue)
        {
            splitValueCount = 0;
            bytesPerValue = 0;
            if (state.DefinitionLevelsByteLength != 0 || state.RepetitionLevelsByteLength != 0)
                return false;
            if (_columns[ordinal].Options.Repetition is not ParquetRepetition.Required and not ParquetRepetition.Unspecified)
                return false;
            if (_columns[ordinal].PhysicalType == ParquetPhysicalType.Boolean)
                return false;
            if (!ColumnCodec.TryGetFixedWidthBytes(_columns[ordinal].PhysicalType, out bytesPerValue))
                return false;
            if (state.ValueCount <= 1)
                return false;

            var byValues = Options.MaxPageValueCount > 0 ? Options.MaxPageValueCount : int.MaxValue;
            var byBytes = Options.MaxPageBytes > 0 ? Math.Max(1, Options.MaxPageBytes / bytesPerValue) : int.MaxValue;
            splitValueCount = Math.Min(byValues, byBytes);
            if (splitValueCount <= 0)
                splitValueCount = 1;
            return splitValueCount < state.ValueCount;
        }

        bool CanUseVariableWidthRequiredSplit(int ordinal, ref ColumnState state)
        {
            if (state.DefinitionLevelsByteLength != 0 || state.RepetitionLevelsByteLength != 0)
                return false;
            if (_columns[ordinal].Options.Repetition is not ParquetRepetition.Required and not ParquetRepetition.Unspecified)
                return false;
            if (_columns[ordinal].PhysicalType is not ParquetPhysicalType.ByteArray)
                return false;
            if (state.ValueCount <= 1)
                return false;

            var hasByteLimit = Options.MaxPageBytes > 0 && Options.MaxPageBytes < int.MaxValue;
            var hasCountLimit = Options.MaxPageValueCount > 0 && Options.MaxPageValueCount < int.MaxValue;
            return hasByteLimit || hasCountLimit;
        }

        bool TryCreateLevelFixedWidthSplitPlan(ParquetWriter writer, int ordinal, ref ColumnState state,
            out int bytesPerValue)
        {
            bytesPerValue = 0;
            if (state.DefinitionLevelsByteLength == 0 && state.RepetitionLevelsByteLength == 0)
                return false;
            if (_columns[ordinal].PhysicalType == ParquetPhysicalType.Boolean)
                return false;
            if (!ColumnCodec.TryGetFixedWidthBytes(_columns[ordinal].PhysicalType, out bytesPerValue))
                return false;
            if (state.ValueCount <= 1)
                return false;
            var hasValueLimit = Options.MaxPageValueCount > 0 && Options.MaxPageValueCount < int.MaxValue;
            var hasByteLimit = Options.MaxPageBytes > 0 && Options.MaxPageBytes < int.MaxValue;
            return hasValueLimit || hasByteLimit;
        }

        void WriteSplitPages(ParquetWriter writer, int ordinal, ref ColumnState state, int splitValueCount, int bytesPerValue, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;
            var valuesRemaining = state.ValueCount;
            var currentValueOffset = 0;
            while (valuesRemaining > 0)
            {
                var pageValueCount = Math.Min(valuesRemaining, splitValueCount);
                var pageEncodedOffset = currentValueOffset * bytesPerValue;
                var pageEncodedLength = pageValueCount * bytesPerValue;
                WritePage(
                    writer,
                    ordinal,
                    ref state,
                    pageValueCount,
                    pageValueCount,
                    nullCount: 0,
                    definitionLevelsByteLength: 0,
                    repetitionLevelsByteLength: 0,
                    pageEncodedOffset,
                    pageEncodedLength,
                    ref totalUncompressedSize,
                    ref totalCompressedSize);
                currentValueOffset += pageValueCount;
                valuesRemaining -= pageValueCount;
            }
        }

        void WriteSinglePage(ParquetWriter writer, int ordinal, ref ColumnState state, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;
            WritePage(
                writer,
                ordinal,
                ref state,
                state.ValueCount,
                state.RowCount,
                state.NullCount,
                state.DefinitionLevelsByteLength,
                state.RepetitionLevelsByteLength,
                encodedOffset: 0,
                encodedLength: state.EncodedLength,
                ref totalUncompressedSize,
                ref totalCompressedSize);
        }

        void WriteSplitVariableWidthPages(ParquetWriter writer, int ordinal, ref ColumnState state, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;

            var source = GetSourceSpan(ref state, 0, state.EncodedLength);
            var maxValues = Options.MaxPageValueCount > 0 ? Options.MaxPageValueCount : int.MaxValue;
            var maxBytes = Options.MaxPageBytes > 0 ? Options.MaxPageBytes : int.MaxValue;

            var valueIndex = 0;
            var payloadOffset = 0;
            while (valueIndex < state.ValueCount)
            {
                var pageStartOffset = payloadOffset;
                var pageBytes = 0;
                var pageValues = 0;

                while (valueIndex < state.ValueCount)
                {
                    if (payloadOffset > source.Length - sizeof(int))
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");

                    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(payloadOffset, sizeof(int)));
                    if (valueLength < 0)
                        throw new InvalidOperationException("Negative byte-array value length is invalid.");
                    var entryLength = checked(sizeof(int) + valueLength);
                    if (payloadOffset > source.Length - entryLength)
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");

                    var wouldExceedCount = pageValues >= maxValues;
                    var wouldExceedBytes = pageValues > 0 && pageBytes > maxBytes - entryLength;
                    if (wouldExceedCount || wouldExceedBytes)
                        break;

                    payloadOffset += entryLength;
                    pageBytes += entryLength;
                    pageValues++;
                    valueIndex++;
                }

                if (pageValues == 0)
                {
                    // Soft limit: always emit at least one full value, even if it's larger than page target.
                    if (payloadOffset > source.Length - sizeof(int))
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");
                    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(payloadOffset, sizeof(int)));
                    if (valueLength < 0)
                        throw new InvalidOperationException("Negative byte-array value length is invalid.");
                    var entryLength = checked(sizeof(int) + valueLength);
                    if (payloadOffset > source.Length - entryLength)
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");

                    payloadOffset += entryLength;
                    pageBytes += entryLength;
                    pageValues = 1;
                    valueIndex++;
                }

                WritePage(
                    writer,
                    ordinal,
                    ref state,
                    valueCount: pageValues,
                    rowCount: pageValues,
                    nullCount: 0,
                    definitionLevelsByteLength: 0,
                    repetitionLevelsByteLength: 0,
                    encodedOffset: pageStartOffset,
                    encodedLength: pageBytes,
                    ref totalUncompressedSize,
                    ref totalCompressedSize);

            }
        }

        void WriteSplitLevelFixedWidthPages(ParquetWriter writer, int ordinal, ref ColumnState state, int bytesPerValue, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;

            var isRepeated = state.RepetitionLevelsByteLength > 0;
            var maxDefLevel = GetMaxDefinitionLevel(writer, ordinal, isRepeated);
            var levelCount = state.ValueCount;
            if (levelCount <= 0)
                return;

            var repLevels = isRepeated ? new byte[levelCount] : Array.Empty<byte>();
            if (isRepeated)
            {
                var repSource = GetSourceSpan(ref state, 0, state.RepetitionLevelsByteLength);
                DecodeBitPackedLevels(repSource, levelCount, 1, repLevels);
            }

            var defLevels = maxDefLevel > 0 ? new byte[levelCount] : Array.Empty<byte>();
            if (maxDefLevel > 0)
            {
                var defSource = GetSourceSpan(ref state, state.RepetitionLevelsByteLength, state.DefinitionLevelsByteLength);
                var defBitWidth = maxDefLevel == 1 ? 1 : 2;
                DecodeBitPackedLevels(defSource, levelCount, defBitWidth, defLevels);
            }

            var dataSourceOffset = state.RepetitionLevelsByteLength + state.DefinitionLevelsByteLength;
            var source = GetSourceSpan(ref state, dataSourceOffset, state.EncodedLength - dataSourceOffset);

            var maxValues = Options.MaxPageValueCount > 0 ? Options.MaxPageValueCount : int.MaxValue;
            var maxBytes = Options.MaxPageBytes > 0 ? Options.MaxPageBytes : int.MaxValue;
            var levelIndex = 0;
            var definedBefore = 0;
            while (levelIndex < levelCount)
            {
                var pageStart = levelIndex;
                var pageLevels = 0;
                var pageRows = 0;
                var pageNulls = 0;
                var pageDefined = 0;
                var overflow = false;
                while (levelIndex < levelCount)
                {
                    if (!isRepeated || repLevels[levelIndex] == 0)
                        pageRows++;

                    var def = maxDefLevel == 0 ? (byte)0 : defLevels[levelIndex];
                    if (maxDefLevel > 0 && def < maxDefLevel)
                        pageNulls++;
                    else
                        pageDefined++;

                    pageLevels++;
                    levelIndex++;

                    if (overflow)
                    {
                        if (!isRepeated || levelIndex >= levelCount || repLevels[levelIndex] == 0)
                            break;
                        continue;
                    }

                    var estBytes = checked((pageDefined * bytesPerValue) + GetLevelEncodedSize(pageLevels, isRepeated ? 1 : 0) + GetLevelEncodedSize(pageLevels, maxDefLevel == 0 ? 0 : (maxDefLevel == 1 ? 1 : 2)));
                    if (pageLevels >= maxValues || estBytes >= maxBytes)
                    {
                        if (!isRepeated)
                            break;
                        overflow = true;
                    }
                }

                var pageRepLen = isRepeated ? GetLevelEncodedSize(pageLevels, 1) : 0;
                var defBitWidth = maxDefLevel == 0 ? 0 : (maxDefLevel == 1 ? 1 : 2);
                var pageDefLen = GetLevelEncodedSize(pageLevels, defBitWidth);
                var pageDataLen = checked(pageDefined * bytesPerValue);
                var pageLen = checked(pageRepLen + pageDefLen + pageDataLen);
                var pageBuffer = ArrayPool<byte>.Shared.Rent(pageLen);
                try
                {
                    var pageSpan = pageBuffer.AsSpan(0, pageLen);
                    var offset = 0;
                    if (isRepeated)
                        offset += WriteBitPackedLevels(repLevels.AsSpan(pageStart, pageLevels), 1, pageSpan[offset..]);
                    if (defBitWidth > 0)
                        offset += WriteBitPackedLevels(defLevels.AsSpan(pageStart, pageLevels), defBitWidth, pageSpan[offset..]);

                    var definedIndex = definedBefore;
                    for (var i = 0; i < pageLevels; i++)
                    {
                        var level = maxDefLevel == 0 ? (byte)0 : defLevels[pageStart + i];
                        if (maxDefLevel > 0 && level < maxDefLevel)
                            continue;

                        var srcOffset = checked(definedIndex * bytesPerValue);
                        source.Slice(srcOffset, bytesPerValue).CopyTo(pageSpan[offset..]);
                        offset += bytesPerValue;
                        definedIndex++;
                    }

                    WritePageFromPayload(
                        writer,
                        ordinal,
                        ref state,
                        pageSpan,
                        pageLevels,
                        pageRows,
                        pageNulls,
                        pageDefLen,
                        pageRepLen,
                        ref totalUncompressedSize,
                        ref totalCompressedSize);
                    definedBefore = definedIndex;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pageBuffer);
                }
            }
        }

        void WritePageFromPayload(ParquetWriter writer, int ordinal, ref ColumnState state, ReadOnlySpan<byte> payload,
            int valueCount, int rowCount, int nullCount, int definitionLevelsByteLength, int repetitionLevelsByteLength,
            ref long totalUncompressedSize, ref long totalCompressedSize)
        {
            var levelsByteLength = checked(definitionLevelsByteLength + repetitionLevelsByteLength);
            if (levelsByteLength > payload.Length)
                throw new InvalidOperationException("Data page level section exceeds payload length.");

            var levelsPayload = payload[..levelsByteLength];
            var dataPayload = payload[levelsByteLength..];
            var compressedDataPayload = dataPayload;
            var compressedDataLength = dataPayload.Length;
            var isCompressed = false;
            if (state.Compression != CompressionKind.None && dataPayload.Length > 0)
            {
                compressedDataLength = CompressPayload(dataPayload, ordinal, ref state, state.Compression, Options.MaxCompressedBytes);
                compressedDataPayload = state.CompressedBufferOwner!.Memory.Span[..compressedDataLength];
                isCompressed = true;
            }

            var uncompressedPayloadLength = checked(levelsByteLength + dataPayload.Length);
            var compressedPayloadLength = checked(levelsByteLength + compressedDataLength);

            var headerLength = ParquetThriftWriter.WriteDataPageHeader(
                _pageHeaderBuffer,
                valueCount,
                nullCount,
                rowCount,
                state.Encoding,
                definitionLevelsByteLength,
                repetitionLevelsByteLength,
                uncompressedSize: uncompressedPayloadLength,
                compressedSize: compressedPayloadLength,
                isCompressed);
            writer.WriteToStream(_pageHeaderBuffer.AsSpan(0, headerLength));
            writer.AdvancePosition(headerLength);
            totalUncompressedSize = checked(totalUncompressedSize + headerLength + uncompressedPayloadLength);
            totalCompressedSize = checked(totalCompressedSize + headerLength + compressedPayloadLength);
            if (compressedPayloadLength == 0)
                return;
            if (levelsPayload.Length > 0)
                writer.WriteToStream(levelsPayload);
            if (compressedDataLength > 0)
                writer.WriteToStream(compressedDataPayload);
            writer.AdvancePosition(compressedPayloadLength);
        }

        void WritePage(ParquetWriter writer, int ordinal, ref ColumnState state, int valueCount, int rowCount, int nullCount, int definitionLevelsByteLength, int repetitionLevelsByteLength, int encodedOffset, int encodedLength, ref long totalUncompressedSize, ref long totalCompressedSize)
        {
            var payload = GetSourceSpan(ref state, encodedOffset, encodedLength);
            WritePageFromPayload(writer, ordinal, ref state, payload, valueCount, rowCount, nullCount,
                definitionLevelsByteLength, repetitionLevelsByteLength, ref totalUncompressedSize, ref totalCompressedSize);
        }

        static ReadOnlySpan<byte> GetSourceSpan(ref ColumnState state, int offset, int length)
        {
            if (!state.ExternalData.IsEmpty)
                return state.ExternalData.Span.Slice(offset, length);
            if (state.EncodedBuffer is not null)
                return state.EncodedBuffer.AsSpan(offset, length);
            if (state.EncodedBufferOwner is not null)
                return state.EncodedBufferOwner.Memory.Span.Slice(offset, length);
            throw new InvalidOperationException("Serialized payload is missing.");
        }

        int CompressPayload(ReadOnlySpan<byte> source, int ordinal, ref ColumnState state, CompressionKind compression, int maxCompressedBytes)
        {
            if (state.CompressedBufferOwner is null)
            {
                var bufferName = _compressedBufferNames[ordinal];
                var length = _compressedBufferLengths[ordinal];
                state.CompressedBufferOwner = _bufferPool.Rent(bufferName, length);
            }

            var destination = state.CompressedBufferOwner.Memory.Span;
            if (maxCompressedBytes > 0 && maxCompressedBytes < destination.Length)
                destination = destination[..maxCompressedBytes];
            if (destination.Length == 0)
                throw new InvalidOperationException("Compressed buffer is too small.");

            return compression switch
            {
                CompressionKind.Brotli => CompressWithBrotli(source, destination),
                CompressionKind.Gzip => CompressWithGzip(source, destination),
                CompressionKind.Snappy => CompressWithSnappy(source, destination),
                CompressionKind.Lz4 => CompressWithLz4(source, destination),
                CompressionKind.Zstd => CompressWithZstd(source, destination),
                CompressionKind.None => source.Length,
                _ => throw new NotSupportedException($"Compression '{compression}' is not supported yet.")
            };
        }

        static int CompressWithBrotli(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (!BrotliEncoder.TryCompress(source, destination, out var written))
                throw new InvalidOperationException("Brotli compressed payload exceeds MaxCompressedBytes.");
            return written;
        }

        static int CompressWithGzip(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            using var buffer = new MemoryStream(destination.Length);
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(source);

            var written = checked((int)buffer.Length);
            if (written > destination.Length)
                throw new InvalidOperationException("Gzip compressed payload exceeds MaxCompressedBytes.");
            buffer.Position = 0;
            var read = buffer.Read(destination);
            if (read != written)
                throw new InvalidOperationException("Could not copy compressed payload.");
            return written;
        }

        static int CompressWithSnappy(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            var maxLength = Snappy.GetMaxCompressedLength(source.Length);
            if (maxLength > destination.Length)
                throw new InvalidOperationException("Snappy compressed payload exceeds MaxCompressedBytes.");

            var written = Snappy.Compress(source, destination);
            if (written > destination.Length)
                throw new InvalidOperationException("Snappy compressed payload exceeds MaxCompressedBytes.");
            return written;
        }

        static int CompressWithLz4(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            var maxLength = LZ4Codec.MaximumOutputSize(source.Length);
            if (maxLength > destination.Length)
                throw new InvalidOperationException("Lz4 compressed payload exceeds MaxCompressedBytes.");

            var written = LZ4Codec.Encode(source, destination);
            if (written <= 0 || written > destination.Length)
                throw new InvalidOperationException("Lz4 compression failed or exceeded MaxCompressedBytes.");
            return written;
        }

        int CompressWithZstd(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (_zstdCompressor is null)
                throw new InvalidOperationException("Zstd compressor is not initialized.");

            var written = _zstdCompressor.Wrap(source, destination);
            if (written <= 0 || written > destination.Length)
                throw new InvalidOperationException("Zstd compression failed or exceeded MaxCompressedBytes.");
            return written;
        }

        static int GetLevelEncodedSize(int valueCount, int bitWidth)
        {
            if (bitWidth == 0 || valueCount == 0)
                return 0;
            var groupCount = (valueCount + 7) >> 3;
            var header = (uint)((groupCount << 1) | 1);
            return GetVarUInt32Length(header) + (groupCount * bitWidth);
        }

        static int GetVarUInt32Length(uint value)
        {
            var length = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                length++;
            }

            return length;
        }

        static void DecodeBitPackedLevels(ReadOnlySpan<byte> source, int valueCount, int bitWidth, Span<byte> destination)
        {
            if (valueCount == 0)
                return;

            var offset = 0;
            _ = ReadVarUInt32(source, ref offset);
            var groups = (valueCount + 7) >> 3;
            if (bitWidth == 1)
            {
                for (var group = 0; group < groups; group++)
                {
                    var packed = source[offset++];
                    for (var i = 0; i < 8; i++)
                    {
                        var index = (group << 3) + i;
                        if (index >= valueCount)
                            break;
                        destination[index] = (byte)((packed >> i) & 1);
                    }
                }
                return;
            }

            for (var group = 0; group < groups; group++)
            {
                var packed = (ushort)(source[offset] | (source[offset + 1] << 8));
                offset += 2;
                for (var i = 0; i < 8; i++)
                {
                    var index = (group << 3) + i;
                    if (index >= valueCount)
                        break;
                    destination[index] = (byte)((packed >> (i * 2)) & 0x3);
                }
            }
        }

        static int WriteBitPackedLevels(ReadOnlySpan<byte> levels, int bitWidth, Span<byte> destination)
        {
            if (levels.Length == 0 || bitWidth == 0)
                return 0;

            var groups = (levels.Length + 7) >> 3;
            var header = (uint)((groups << 1) | 1);
            var offset = WriteVarUInt32(header, destination);
            if (bitWidth == 1)
            {
                for (var group = 0; group < groups; group++)
                {
                    byte packed = 0;
                    for (var i = 0; i < 8; i++)
                    {
                        var index = (group << 3) + i;
                        if (index >= levels.Length)
                            break;
                        packed |= (byte)((levels[index] & 1) << i);
                    }
                    destination[offset++] = packed;
                }
                return offset;
            }

            for (var group = 0; group < groups; group++)
            {
                ushort packed = 0;
                for (var i = 0; i < 8; i++)
                {
                    var index = (group << 3) + i;
                    if (index >= levels.Length)
                        break;
                    packed |= (ushort)((levels[index] & 0x3) << (i * 2));
                }
                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
            }
            return offset;
        }

        static int WriteVarUInt32(uint value, Span<byte> destination)
        {
            var offset = 0;
            while (value >= 0x80)
            {
                destination[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }
            destination[offset++] = (byte)value;
            return offset;
        }

        static uint ReadVarUInt32(ReadOnlySpan<byte> source, ref int offset)
        {
            uint value = 0;
            var shift = 0;
            while (true)
            {
                var current = source[offset++];
                value |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return value;
                shift += 7;
            }
        }

        static int GetMaxDefinitionLevel(ParquetWriter writer, int ordinal, bool isRepeated)
        {
            if (!isRepeated)
                return 1;
            return writer._repeatedElementStates[ordinal] == 2 ? 2 : 1;
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

        void RegisterBuffers(RowGroupOptions options, uint? rowGroupRowCountHint)
        {
            if (_columns.Length == 0)
                return;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var targetLength = DefaultEncodedBufferBytes;
            for (var i = 0; i < _columns.Length; i++)
            {
                var column = _columns[i];
                var length = targetLength;
                if (rowGroupRowCountHint.HasValue)
                {
                    var rowCount = checked((int)rowGroupRowCountHint.Value);
                    if (ColumnCodec.TryGetFixedWidthBytes(column.PhysicalType, out var width))
                    {
                        var hintLength = checked(rowCount * width);
                        if (column.Options.Repetition is ParquetRepetition.Optional)
                            hintLength = checked(hintLength + ColumnCodec.GetDefinitionLevelsByteCount(rowCount));
                        if (hintLength > length)
                            length = hintLength;
                    }
                    else if (column.PhysicalType is ParquetPhysicalType.ByteArray)
                    {
                        var variableWidthHint = checked(rowCount * 8);
                        if (column.Options.Repetition is ParquetRepetition.Optional)
                            variableWidthHint = checked(variableWidthHint + ColumnCodec.GetDefinitionLevelsByteCount(rowCount));
                        if (variableWidthHint > length)
                            length = variableWidthHint;
                    }
                }

                var bucketName = BuildColumnBucketName(column, length);
                _bufferNames[i] = bucketName;
                _bufferLengths[i] = length;
                var compressedLength = GetCompressedBufferLength(length, options.MaxCompressedBytes, _compressionKind);
                var compressedBucketName = BuildCompressedBucketName(column, compressedLength);
                _compressedBufferNames[i] = compressedBucketName;
                _compressedBufferLengths[i] = compressedLength;
                if (counts.TryGetValue(bucketName, out var count))
                    counts[bucketName] = count + 1;
                else
                    counts[bucketName] = 1;
                if (counts.TryGetValue(compressedBucketName, out var compressedCount))
                    counts[compressedBucketName] = compressedCount + 1;
                else
                    counts[compressedBucketName] = 1;
            }

            foreach (var entry in counts)
                _bufferPool.Register(entry.Key, GetBucketLength(entry.Key, _bufferNames, _bufferLengths, _compressedBufferNames, _compressedBufferLengths), entry.Value);
        }

        static string BuildColumnBucketName(Column column, int length)
            => $"column:{column.PhysicalType}:{length}";

        static string BuildCompressedBucketName(Column column, int length)
            => $"column-compressed:{column.PhysicalType}:{length}";

        static int GetCompressedBufferLength(int uncompressedLength, int maxCompressedBytes, CompressionKind compression)
        {
            if (maxCompressedBytes > 0)
                return maxCompressedBytes;
            if (compression == CompressionKind.None)
                return uncompressedLength;

            var required = compression switch
            {
                CompressionKind.Snappy => Snappy.GetMaxCompressedLength(uncompressedLength),
                CompressionKind.Lz4 => LZ4Codec.MaximumOutputSize(uncompressedLength),
                CompressionKind.Gzip => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
                CompressionKind.Brotli => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
                CompressionKind.Zstd => checked(uncompressedLength + Math.Max(256, uncompressedLength >> 3)),
                _ => uncompressedLength
            };

            return required < uncompressedLength ? uncompressedLength : required;
        }

        static int GetBucketLength(string bucketName, string[] names, int[] lengths, string[] compressedNames, int[] compressedLengths)
        {
            for (var i = 0; i < names.Length; i++)
                if (StringComparer.Ordinal.Equals(names[i], bucketName))
                    return lengths[i];

            for (var i = 0; i < compressedNames.Length; i++)
                if (StringComparer.Ordinal.Equals(compressedNames[i], bucketName))
                    return compressedLengths[i];

            throw new InvalidOperationException($"Bucket '{bucketName}' length was not found.");
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
