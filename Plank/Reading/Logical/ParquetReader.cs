using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Reading.Logical;

public sealed class ParquetReader : IDisposable
{
    readonly ParquetReaderOptions _options;
    readonly ParquetSchema? _requestedSchema;
    internal readonly ParquetFileReader PhysicalReader;
    ParquetSchema _schema;
    InternalParquetFooter _footer;
    ParquetFileMetadata _metadata;
    int _footerVersion;
    bool _disposed;

    public ParquetReader(ParquetReaderOptions? options = null)
        : this(options, requestedSchema: null)
    {
    }

    internal ParquetReader(ParquetSchema requestedSchema, ParquetReaderOptions? options = null)
        : this(options, RequireRequestedSchema(requestedSchema))
    {
    }

    ParquetReader(ParquetReaderOptions? options, ParquetSchema? requestedSchema)
    {
        var readerOptions = options ?? ParquetReaderOptions.Default;
        readerOptions.Validate();

        _options = readerOptions;
        _requestedSchema = requestedSchema;
        PhysicalReader = new ParquetFileReader(new ParquetFileReaderOptions
        {
            BufferPool = readerOptions.BufferPool
        });
        _schema = new ParquetSchema(System.Collections.Immutable.ImmutableArray<Column>.Empty);
        _footer = InternalParquetFooter.Empty;
        _metadata = default;
        _footerVersion = 0;
        _disposed = false;
    }

    public ParquetSchema Schema
        => _schema;

    public ParquetFileMetadata Metadata
        => _metadata;

    public ParquetFooter Footer
        => new(this);

    internal ParquetReaderOptions Options
        => _options;

    public void Reset(Stream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        PhysicalReader.Reset(stream);
        ResetLogicalState();
    }

    public void Reset(IParquetReadSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        PhysicalReader.Reset(source);
        ResetLogicalState();
    }

    void ResetLogicalState()
    {
        var physicalMetadata = PhysicalReader.Metadata;
        var fileSchema = PhysicalSchemaBinder.BuildSchema(physicalMetadata);
        if (_options.Strict && _requestedSchema is not null)
            ValidateRequestedSchema(_requestedSchema, fileSchema);

        var schema = _requestedSchema ?? fileSchema;
        var footerVersion = _footerVersion + 1;
        var footer = PhysicalSchemaBinder.Bind(PhysicalReader, schema, _footer, _options.Strict,
            _options.BufferPool, footerVersion);

        _schema = schema;
        _footer = footer;
        _metadata = new ParquetFileMetadata(fileSchema, physicalMetadata.FooterOffset,
            physicalMetadata.FooterLength, physicalMetadata.FileVersion);
        _footerVersion = footerVersion;
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
        PhysicalReader.Dispose();
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetReader));
    }

    internal bool TryReadNextRowGroupToken(int ordinal, out RowGroupToken token)
    {
        ThrowIfDisposed();
        if ((uint)ordinal >= _footer.RowGroupCount)
        {
            token = default;
            return false;
        }

        var rowGroup = _footer.RowGroups[ordinal];
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

    static ParquetSchema RequireRequestedSchema(ParquetSchema schema)
        => schema ?? throw new ArgumentNullException(nameof(schema));
}
