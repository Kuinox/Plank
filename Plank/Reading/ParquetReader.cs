using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Reading;

public sealed class ParquetReader : IDisposable
{
    static ReadOnlySpan<byte> FileMagic
        => "PAR1"u8;

    readonly ParquetReaderOptions _options;
    readonly ParquetSchema? _requestedSchema;
    ParquetSchema _schema;
    InternalParquetFooter _footer;
    ParquetFileMetadata _metadata;
    byte[] _footerBuffer;
    int _footerLength;
    ulong _rowGroupsMetadataOffset;
    InternalColumnChunkMetadata[] _rowGroupEnumerationColumns;
    IParquetReadSource _source;
    StreamReadSource? _streamSource;
    int _footerVersion;
    bool _disposed;

    ParquetReader(IParquetReadSource source, StreamReadSource? streamSource, ParquetReaderOptions options,
        ParquetSchema? requestedSchema = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _requestedSchema = requestedSchema;
        _schema = new ParquetSchema(System.Collections.Immutable.ImmutableArray<Column>.Empty);
        _footer = InternalParquetFooter.Empty;
        _metadata = default;
        _footerBuffer = [];
        _footerLength = 0;
        _rowGroupsMetadataOffset = 0;
        _rowGroupEnumerationColumns = [];
        _source = source;
        _streamSource = streamSource;
        _footerVersion = 0;
        _disposed = false;
        Reset(source);
    }

    public static ParquetReader Open(Stream stream, ParquetReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var source = new StreamReadSource(stream);
        return new ParquetReader(source, source, options ?? ParquetReaderOptions.Default);
    }

    public static ParquetReader Open(IParquetReadSource source, ParquetReaderOptions? options = null)
        => new(source, source as StreamReadSource, options ?? ParquetReaderOptions.Default);

    internal static ParquetReader Open(IParquetReadSource source, ParquetSchema requestedSchema,
        ParquetReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(requestedSchema);
        return new ParquetReader(source, source as StreamReadSource, options ?? ParquetReaderOptions.Default,
            requestedSchema);
    }

    public ParquetSchema Schema
        => _schema;

    public ParquetFileMetadata Metadata
        => _metadata;

    public ParquetFooter Footer
        => new(this);

    internal ParquetReaderOptions Options
        => _options;

    internal IParquetReadSource Source
        => _source;

    internal ReadOnlySpan<byte> FooterBytes
        => _footerBuffer.AsSpan(0, _footerLength);

    internal ulong RowGroupsMetadataOffset
        => _rowGroupsMetadataOffset;

    internal int RowGroupsOffset
        => 0;

    internal int FooterVersion
        => _footerVersion;

    public void Reset(Stream stream)
    {
        ThrowIfDisposed();
        if (_streamSource is null)
            _streamSource = new StreamReadSource(stream);
        else
            _streamSource.Reset(stream);
        Reset(_streamSource);
    }

    public void Reset(IParquetReadSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        if (source is StreamReadSource streamSource)
            _streamSource = streamSource;

        if (source.Length < 12)
            throw new CorruptParquetException("Stream is too small to contain a Parquet footer.");

        Span<byte> trailer = stackalloc byte[8];
        source.ReadExactly(source.Length - (ulong)trailer.Length, trailer);
        if (!trailer[4..].SequenceEqual(FileMagic))
            throw new CorruptParquetException("Stream does not end with the PAR1 footer marker.");

        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(trailer[..4]);
        if (footerLength > source.Length - (ulong)trailer.Length)
            throw new CorruptParquetException("Footer length exceeds stream size.");

        var footerOffset = source.Length - (ulong)trailer.Length - footerLength;
        if (footerOffset < 4)
            throw new CorruptParquetException("Footer offset is invalid for this stream.");

        _footerLength = checked((int)footerLength);
        if (_footerBuffer.Length < _footerLength)
            _footerBuffer = new byte[_footerLength];
        var footerBytes = _footerBuffer.AsSpan(0, _footerLength);
        source.ReadExactly(footerOffset, footerBytes);

        var footer = ParquetMetadataThriftReader.Read(footerBytes);
        if (_options.Strict && _requestedSchema is not null)
            ValidateRequestedSchema(_requestedSchema, footer.Schema);

        var rowGroupsLength = footer.RowGroupsEndOffset - footer.RowGroupsOffset;
        if (rowGroupsLength < 0)
            throw new CorruptParquetException("Footer row group range is invalid.");
        if (rowGroupsLength > 0)
            footerBytes.Slice(footer.RowGroupsOffset, rowGroupsLength).CopyTo(_footerBuffer);

        _footerLength = rowGroupsLength;
        _rowGroupsMetadataOffset = footerOffset + (ulong)footer.RowGroupsOffset;
        _footer = new InternalParquetFooter(footer.Version, footer.Schema, footer.RowGroupCount, 0, rowGroupsLength);
        _schema = _requestedSchema ?? _footer.Schema;
        _metadata = new ParquetFileMetadata(_footer.Schema, footerOffset, footerLength, _footer.Version);
        _footerVersion++;
    }

    public RowGroupTokenEnumerable EnumerateRowGroups()
    {
        ThrowIfDisposed();
        return new RowGroupTokenEnumerable(this);
    }

    public RowGroupReader OpenRowGroup(RowGroupToken token)
    {
        ThrowIfDisposed();
        var rowGroup = CreateReusableRowGroupReader();
        return OpenRowGroup(token, rowGroup);
    }

    public RowGroupReader CreateReusableRowGroupReader()
        => new(new RowGroupReadContext(_schema.Columns.Length));

    public RowGroupReader OpenRowGroup(RowGroupToken token, RowGroupReader reusable)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(reusable);

        ValidateRowGroupToken(token);
        reusable.Reset(this, token.Metadata);
        return reusable;
    }

    void ValidateRowGroupToken(RowGroupToken token)
    {
        if (!ReferenceEquals(token.Reader, this))
            throw new ArgumentException("Row group token does not belong to this reader.", nameof(token));
        if (token.RowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(token), token.RowGroupOrdinal, "Row group ordinal must be non-negative.");
        if ((uint)token.RowGroupOrdinal >= _footer.RowGroupCount)
            throw new ArgumentOutOfRangeException(nameof(token), token.RowGroupOrdinal, "Row group ordinal is outside the parsed footer.");
        if (token.FooterVersion != _footerVersion)
            throw new ArgumentException("Row group token does not belong to the current reader state.", nameof(token));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetReader));
    }

    internal bool TryReadNextRowGroupToken(int ordinal, ref int offset, out RowGroupToken token)
    {
        ThrowIfDisposed();
        if ((uint)ordinal >= _footer.RowGroupCount)
        {
            token = default;
            return false;
        }

        if (!ParquetMetadataThriftReader.TryReadNextRowGroup(FooterBytes, _rowGroupsMetadataOffset,
                _footer.RowGroupsEndOffset, ordinal, ref offset, _rowGroupEnumerationColumns, _schema, _footerVersion,
                out var rowGroup))
        {
            token = default;
            return false;
        }

        _rowGroupEnumerationColumns = rowGroup.Columns;
        token = new RowGroupToken(this, rowGroup);
        return true;
    }

    internal int GetColumnOrdinal(Column column)
    {
        var ordinal = _schema.Columns.IndexOf(column);
        if (ordinal >= 0)
            return ordinal;

        for (var i = 0; i < _schema.Columns.Length; i++)
            if (_schema.Columns[i].Name == column.Name && _schema.Columns[i].PhysicalType == column.PhysicalType)
                return i;

        throw new ArgumentException("Column does not belong to this schema.", nameof(column));
    }

    static void ValidateRequestedSchema(ParquetSchema requestedSchema, ParquetSchema fileSchema)
    {
        var fileColumns = fileSchema.Columns;
        var fileColumnByPath = new Dictionary<string, Column>(fileColumns.Length, StringComparer.Ordinal);
        for (var i = 0; i < fileColumns.Length; i++)
            if (!fileColumnByPath.TryAdd(fileColumns[i].Name, fileColumns[i]))
                throw new CorruptParquetException($"File schema contains duplicate leaf path '{fileColumns[i].Name}'.");

        var requestedColumns = requestedSchema.Columns;
        for (var i = 0; i < requestedColumns.Length; i++)
        {
            var requestedColumn = requestedColumns[i];
            if (!fileColumnByPath.TryGetValue(requestedColumn.Name, out var fileColumn))
                throw new InvalidOperationException(
                    $"Requested schema column '{requestedColumn.Name}' is not present in the file schema.");

            ValidateRequestedColumn(requestedColumn, fileColumn);
        }
    }

    static void ValidateRequestedColumn(Column requestedColumn, Column fileColumn)
    {
        if (requestedColumn.PhysicalType != fileColumn.PhysicalType)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has physical type {requestedColumn.PhysicalType}, but file schema has {fileColumn.PhysicalType}.");

        var requestedRepetition = NormalizeRepetition(requestedColumn.Options.Repetition);
        var fileRepetition = NormalizeRepetition(fileColumn.Options.Repetition);
        if (fileRepetition == ParquetRepetition.Optional && requestedRepetition == ParquetRepetition.Required)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' is required, but the file schema column is optional.");
        if ((fileRepetition == ParquetRepetition.Repeated || requestedRepetition == ParquetRepetition.Repeated) &&
            fileRepetition != requestedRepetition)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has repetition {requestedRepetition}, but file schema has {fileRepetition}.");

        if (requestedColumn.PhysicalType == ParquetPhysicalType.FixedLenByteArray &&
            requestedColumn.Options.TypeLength != fileColumn.Options.TypeLength)
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has fixed length {requestedColumn.Options.TypeLength}, but file schema has {fileColumn.Options.TypeLength}.");

        if (!AreLogicalTypesCompatible(requestedColumn, fileColumn.LogicalType))
            throw new InvalidOperationException(
                $"Requested schema column '{requestedColumn.Name}' has logical type {DescribeLogicalType(requestedColumn.LogicalType)}, but file schema has {DescribeLogicalType(fileColumn.LogicalType)}.");
    }

    static ParquetRepetition NormalizeRepetition(ParquetRepetition repetition)
        => repetition == ParquetRepetition.Unspecified ? ParquetRepetition.Required : repetition;

    static bool AreLogicalTypesCompatible(Column requestedColumn, LogicalType? fileLogicalType)
    {
        if (Equals(requestedColumn.LogicalType, fileLogicalType))
            return true;

        if (requestedColumn.LogicalType is null && fileLogicalType is LogicalType.Int integer && integer.IsSigned)
            return (requestedColumn.PhysicalType, integer.BitWidth) is
                (ParquetPhysicalType.Int32, 32) or
                (ParquetPhysicalType.Int64, 64);

        return false;
    }

    static string DescribeLogicalType(LogicalType? logicalType)
        => logicalType?.ToString() ?? "none";
}
