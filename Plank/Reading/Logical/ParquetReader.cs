using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Reading.Logical;

public sealed class ParquetReader : IDisposable
{
    readonly ParquetSchema? _requestedSchema;
    internal readonly ParquetFileReader PhysicalReader;
    InternalParquetFooter _footer;
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

        Options = readerOptions;
        _requestedSchema = requestedSchema;
        PhysicalReader = new ParquetFileReader(new ParquetFileReaderOptions
        {
            BufferPool = readerOptions.BufferPool
        });
        Schema = new ParquetSchema(System.Collections.Immutable.ImmutableArray<Column>.Empty);
        _footer = InternalParquetFooter.Empty;
        Metadata = default;
        _footerVersion = 0;
        _disposed = false;
    }

    public ParquetSchema Schema { get; private set; }

    public ParquetFileMetadata Metadata { get; private set; }

    public RowGroupCollection RowGroups
    {
        get
        {
            ThrowIfDisposed();
            return new RowGroupCollection(this, _footerVersion);
        }
    }

    internal ParquetReaderOptions Options { get; }

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
        if (Options.Strict && _requestedSchema is not null)
            ValidateRequestedSchema(_requestedSchema, fileSchema);

        var schema = _requestedSchema ?? fileSchema;
        var footerVersion = _footerVersion + 1;
        var footer = PhysicalSchemaBinder.Bind(PhysicalReader, schema, _footer, Options.Strict,
            Options.BufferPool, footerVersion);

        Schema = schema;
        _footer = footer;
        Metadata = new ParquetFileMetadata(fileSchema, physicalMetadata.FooterOffset,
            physicalMetadata.FooterLength, physicalMetadata.FileVersion);
        _footerVersion = footerVersion;
    }

    internal void ValidateRowGroup(RowGroup rowGroup)
    {
        if (!ReferenceEquals(rowGroup.Reader, this))
            throw new ArgumentException("Row group does not belong to this reader.", nameof(rowGroup));
        if (rowGroup.Index < 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroup), rowGroup.Index,
                "Row group index must be non-negative.");
        if ((uint)rowGroup.Index >= _footer.RowGroupCount)
            throw new ArgumentOutOfRangeException(nameof(rowGroup), rowGroup.Index,
                "Row group index is outside the parsed footer.");
        if (rowGroup.Metadata.FooterVersion != _footerVersion)
            throw new ArgumentException("Row group does not belong to the current reader state.", nameof(rowGroup));
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

    internal int GetRowGroupCount(int footerVersion)
    {
        ThrowIfDisposed();
        ValidateFooterVersion(footerVersion);
        return checked((int)_footer.RowGroupCount);
    }

    internal RowGroup GetRowGroup(int index, int footerVersion)
    {
        ThrowIfDisposed();
        ValidateFooterVersion(footerVersion);
        if ((uint)index >= _footer.RowGroupCount)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "Row group index is outside the parsed footer.");
        return new RowGroup(this, _footer.RowGroups[index]);
    }

    void ValidateFooterVersion(int footerVersion)
    {
        if (footerVersion != _footerVersion)
            throw new InvalidOperationException("The row group collection is stale because the reader was reset.");
    }

    internal int GetColumnOrdinal(Column column)
    {
        var ordinal = Schema.Columns.IndexOf(column);
        if (ordinal >= 0)
            return ordinal;

        for (var i = 0; i < Schema.Columns.Length; i++)
            if (Schema.Columns[i].Name == column.Name && Schema.Columns[i].PhysicalType == column.PhysicalType)
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
